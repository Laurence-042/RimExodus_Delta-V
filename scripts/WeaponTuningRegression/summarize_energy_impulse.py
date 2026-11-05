"""Preserve the V4 proposal's selected results and reproducible input hashes."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "Source/obj/WeaponTuningRegression"


def main():
    weapons = json.loads((RAW / "energy-impulse-audit.json").read_text(encoding="utf-8"))
    hull = json.loads((RAW / "energy-impulse-penetration.json").read_text(encoding="utf-8"))
    baseline = json.loads((RAW / "output-audit.json").read_text(encoding="utf-8"))
    result = {
        "status": "Offline candidate only; weapon roles partially satisfied; equal-ship 120-240 seconds NOT established.",
        "random_seed": 170519,
        "weapon_configurations": len(weapons["Rows"]),
        "full_grid_line_cases": weapons["FullGridLineCases"],
        "fragment_carry_checks": 2,
        "weapon_assumptions": weapons["Assumptions"],
        "hull_assumptions": hull["Assumptions"],
        "baseline_provenance": {k: v for k, v in baseline.items() if k.endswith("Sha256")},
        "candidate": weapons["Candidate"],
        "loadouts": weapons["Loadouts"],
        "rails": weapons["Rails"],
        "line": weapons["Line"],
        "guard": weapons["Guard"],
        "native": weapons["Native"],
        "funding": weapons["Boundaries"],
        "max_reflected_budget_ratio": weapons["MaxReflectedBudgetRatio"],
        "depth": hull["Depth"],
        "hull": hull["Results"],
        "rail_energy": hull["RailEnergyTransactions"],
        "source_hashes": {},
    }
    paths = [
        "scripts/WeaponTuningRegression/EnergyImpulseModel.cs",
        "scripts/WeaponTuningRegression/EnergyImpulseAudit.cs",
        "scripts/WeaponTuningRegression/RoleBalanceAudit.cs",
        "scripts/WeaponTuningRegression/ShieldDurationAudit.cs",
        "scripts/RailgunRegression/EnergyImpulsePenetrationAudit.cs",
        "scripts/RailgunRegression/RolePenetrationAudit.cs",
        "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml",
        "1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml",
        "1.6/Assemblies/DeltaV.dll",
        "1.7/Assemblies/DeltaV.dll",
    ]
    for path in paths:
        result["source_hashes"][path] = hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
    for row in result["guard"]:
        row.pop("Trace", None)
    output = ROOT / "scripts/WeaponTuningRegression/results/2026-11-05-energy-impulse.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(output)
    print(f"{len(result['guard'])} guard; {len(result['depth'])} burst; "
          f"{len(result['hull'])} battery; {len(result['rail_energy'])} energy scenarios")


if __name__ == "__main__":
    main()
