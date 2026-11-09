"""Verify production output against the approved V4 standard; retain a compact snapshot.

Run run.ps1 -OutputAudit first. This checks recorded production results, not a
replacement ballistics model. Historical candidate snapshots are never rewritten.
"""
import hashlib
import json
import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OBJ = ROOT / "Source/obj"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def near(actual, expected, label):
    require(math.isclose(actual, expected, rel_tol=2e-6, abs_tol=2e-5),
            f"{label}: {actual} != {expected}")


def main():
    audit = json.loads((OBJ / "WeaponTuningRegression/output-audit.json").read_text())
    sources = {
        "AssemblySha256": "1.7/Assemblies/DeltaV.dll",
        "DefsSha256": "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml",
        "EffectsSha256": "1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml",
        "ProjectileSha256": "1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml",
        "SuppliesSha256": "1.6/Defs/SpaceCombat/DeltaV_GunSupplies.xml",
        "ShieldSha256": "1.6/Defs/SpaceCombat/DeltaV_Shields.xml",
    }
    for field, filename in sources.items():
        require(audit[field] == digest(ROOT / filename), f"Stale audit: {filename}")
    rows = audit["Rows"]
    require(len(rows) == audit["Count"] == 20592, "Incomplete configuration scan")
    require(len(audit["Guards"]) == 352, "Incomplete guard scan")
    require("750+750" in audit["Assumptions"], "Legacy guard fixture")
    maximum_error = 0
    selected = []
    for row in rows:
        if row["Family"] == "Laser":
            scale = 1 if row["Size"] == "Small" else 2
            damage = 8 * scale * row["Power"]
            shield = scale * row["Power"]
            require(row["FirstLockSeconds"] is None, f"Laser heat lock: {row['Key']}")
            selected.append(row)
        else:
            damage, shield = 24 * row["Charge"], 3 * row["Charge"]
        near(row["Damage"], damage, row["Key"] + " structure")
        near(row["ShieldDamage"], shield, row["Key"] + " guard")
        maximum_error = max(maximum_error, abs(row["Damage"] - damage))
        if row["Family"] == "Heavy":
            near(row["SustainedDps"], 96 if row["Size"] == "Small" else 144,
                 row["Key"] + " thermal DPS")
            if row["Charge"] in (1, 24, 48) and row["Heads"] in (1, 24, 32, 48, 96):
                selected.append(row)
        elif row["Family"] == "Rapid" and row["Interval"] == 6:
            near(row["SustainedDps"], 192 if row["Size"] == "Small" else 96 * 29 * 60 / 410,
                 row["Key"] + " thermal DPS")
            selected.append(row)
    # The low-power laser also independently exposes the loaded regular capacity.
    guard = next(g for g in audit["Guards"] if g["Key"] == rows[0]["Key"]
                 and g["Generators"] == 1 and not g["FullEmergency"] and g["UnlimitedAmmo"])
    near(guard["Absorbed"], 750, "Standard guard capacity")
    checks = {}
    for name, filename, count in (
        ("weapons", "v4-runtime-output.log", 392),
        ("collision_energy", "energy-impulse-rail-check.log", 234),
        ("tutorial", "energy-impulse-tutorial-check.log", 74472),
        ("weapon_execution", "energy-impulse-execution-check.log", 427),
    ):
        log = (OBJ / filename).read_text(encoding="utf-8-sig")
        require(re.search(rf"PASS {count}\b", log) is not None, f"Missing completion: {filename}")
        require(re.search(r"^FAIL\b", log, re.M) is None, f"Failed check: {filename}")
        checks[name] = {"passed": count, "log_sha256": digest(OBJ / filename)}
    paths = set(sources.values()) | {"1.6/Assemblies/DeltaV.dll"}
    paths.update(str(p.relative_to(ROOT)).replace("\\", "/")
                 for folder in ("Source/Core", "Source/Buildings", "Source/Interaction")
                 for p in (ROOT / folder).glob("*Space*.cs"))
    for name in ("WeaponRange", "ShieldTarget", "Shipyard"):
        paths.add(f"Common/Prefabs/Tutorial/{name}.xml")
    for filename in list(sources.values())[1:]:
        require(digest(ROOT / filename) == digest(ROOT / filename.replace("1.6/", "1.7/", 1)),
                f"Unsynchronized Def: {filename}")
    result = {
        "Status": "Approved standard implemented; game acceptance pending",
        "Assumptions": audit["Assumptions"],
        "Scope": "Production output/thermal/guard scan and named offline regression suites. Not a full two-sided ship battle.",
        "ConfigurationsChecked": len(rows), "MaximumChemicalDamageError": maximum_error,
        "RegressionChecks": checks, "SelectedConfigurations": selected, "Guards": audit["Guards"],
        "InputSha256": {p: digest(ROOT / p) for p in sorted(paths)},
    }
    output = ROOT / "scripts/WeaponTuningRegression/results/2026-11-05-energy-impulse-runtime.json"
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"PASS {len(rows)} production configurations; 352 current guard scenarios; snapshot: {output}")


if __name__ == "__main__":
    main()
