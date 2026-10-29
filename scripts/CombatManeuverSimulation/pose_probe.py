"""Reproduce a Cosmoteer-style weapon coverage scan for local Delta-V prefabs.

This is a diagnostic, not the game's AI: it uses real prefab mounts and weapon
Defs, but intentionally omits map line of sight, live power/ammo and ballistics.
Ranges use target *center* to agree with Delta-V's current fire gate.
"""

from __future__ import annotations

import argparse
import math
import re
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
DEFS = ROOT / "1.6/Defs/SpaceCombat"
ANGLE_BY_ROTATION = {0: 90.0, 1: 0.0, 2: 270.0, 3: 180.0}


def xy(raw: str) -> tuple[float, float]:
    numbers = [float(n) for n in re.findall(r"[-+]?\d+(?:\.\d+)?", raw)]
    return numbers[0], numbers[-1]


def damage_by_projectile() -> dict[str, float]:
    result: dict[str, float] = {}
    root = ET.parse(DEFS / "DeltaV_SpaceImpactEffects.xml").getroot()
    for effect in root:
        damage = effect.findtext("defenderEffect/damage")
        if damage is None:
            continue
        for projectile in effect.findall("projectileDefs/li"):
            result.setdefault(projectile.text, float(damage))
    return result


def weapons(prefab: str) -> list[dict]:
    defs = ET.parse(DEFS / "DeltaV_SpaceWeapons.xml").getroot()
    by_name = {item.findtext("defName"): item for item in defs.findall("ThingDef")}
    projectile_defs = ET.parse(DEFS / "DeltaV_SpaceProjectiles.xml").getroot()
    projectiles = {item.findtext("defName"): item for item in projectile_defs.findall("ThingDef")}
    damage = damage_by_projectile()
    root = ET.parse(ROOT / "Prefabs" / f"{prefab}.xml").getroot()
    size = xy(root.findtext("prefab/size"))
    center = ((size[0] - 1) / 2, (size[1] - 1) / 2)
    result = []
    for entry in root.findall("thingStates/li"):
        name = entry.findtext("def")
        definition = by_name.get(name)
        if definition is None:
            continue
        comp = next((c for c in definition.findall("comps/li")
                     if c.get("Class") == "DeltaV.CompProperties_SpaceWeapon"), None)
        if comp is None or comp.findtext("aimMode") == "PointDefense" or \
                comp.findtext("manualOnly") == "true":
            continue
        projectile = comp.findtext("projectileDef")
        projectile_def = projectiles.get(projectile)
        projectile_comp = (next((c for c in projectile_def.findall("comps/li")
                                 if c.get("Class") == "DeltaV.CompProperties_SpaceProjectile"), None)
                           if projectile_def is not None else None)
        guided = projectile_comp is not None and projectile_comp.findtext("guided") == "true"
        cold_launch = projectile_comp is not None and \
            int(projectile_comp.findtext("coldLaunchTicks") or 0) > 0
        lock_arc = comp.findtext("lockArcDeg")
        arc = float(comp.findtext("fireArcDeg") or 0)
        if guided or cold_launch:
            arc = min(float(lock_arc) if lock_arc is not None else arc,
                      float(projectile_comp.findtext("lockArcDeg") or 360))
        rate = damage.get(projectile, 20.0) * float(comp.findtext("impactScale") or 1)
        rate *= 60 / max(1, int(comp.findtext("cooldownTicks") or 60))
        position = xy(entry.findtext("pos"))
        result.append({"name": name, "position": (position[0] - center[0],
                                               position[1] - center[1]),
                       "rotation": int(entry.findtext("rotation") or 0),
                       "arc": arc,
                       "min": float(comp.findtext("minRange") or 0),
                       "max": float(comp.findtext("range") or 0),
                       "muzzle_sweep": float(comp.findtext("turretMuzzleDistance") or 0),
                       "rate": rate})
    return result


def coverage(mounts: list[dict], distance: float, heading_deg: float) -> float:
    score = 0.0
    heading = math.radians(heading_deg)
    for mount in mounts:
        x, y = mount["position"]
        muzzle_x = x * math.cos(heading) - y * math.sin(heading)
        muzzle_y = x * math.sin(heading) + y * math.cos(heading)
        dx, dy = distance - muzzle_x, -muzzle_y
        separation = math.hypot(dx, dy)
        if not mount["min"] <= separation <= mount["max"]:
            continue
        facing = (ANGLE_BY_ROTATION[mount["rotation"]] + heading_deg) % 360
        bearing = math.degrees(math.atan2(dy, dx)) % 360
        error = abs((bearing - facing + 180) % 360 - 180)
        if error <= mount["arc"] / 2 + 1e-6:
            score += mount["rate"]
    return score


def report(prefab: str) -> None:
    mounts = weapons(prefab)
    if not mounts:
        print(f"{prefab}: no supported volley weapons")
        return
    headings = range(0, 360, 45)
    max_range = max(m["max"] for m in mounts)
    samples = [(r, h, coverage(mounts, r, h))
               for r in range(30, int(max_range) + 1)
               for h in headings]
    best_score = max(row[2] for row in samples)
    best = max((row for row in samples if row[2] >= best_score - 1e-5),
               key=lambda row: row[0])
    print(f"{prefab}: {len(mounts)} volley weapons, best coverage "
          f"{best_score:.1f} nominal damage/s at center range {best[0]} and heading "
          f"{best[1]} deg (one farthest equal-firepower pose)")
    robust = [(m["max"] - math.hypot(*m["position"]) - m["muzzle_sweep"],
               m["rate"]) for m in mounts]
    robust_ranges = sorted({r for r, _ in robust if r > 0})
    if robust_ranges:
        robust_peak = max(sum(rate for limit, rate in robust if limit >= r)
                          for r in robust_ranges)
        robust_far = max(r for r in robust_ranges
                         if sum(rate for limit, rate in robust if limit >= r)
                         >= robust_peak - 1e-5)
        print(f"  farthest full-firepower range with mount/muzzle allowance: "
              f"{robust_far:.1f}")
    for r in (102, 120, 130, 140, 150, 170):
        if r > max_range:
            continue
        scores = [coverage(mounts, r, h) for h in headings]
        print(f"  range {r:3}: best {max(scores):6.1f}, "
              f"heading variation {min(scores):6.1f}..{max(scores):6.1f}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prefabs", nargs="*", default=["Fighter_0", "Demo_0"],
                        help="prefab filename stems inside Prefabs/")
    for name in parser.parse_args().prefabs:
        report(name)
