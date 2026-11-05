"""Keep a compact, reviewable snapshot of the V3 production-fixture audit."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "Source/obj/WeaponTuningRegression"


def main():
    weapons = json.loads((RAW / "role-balance-audit.json").read_text(encoding="utf-8"))
    hull = json.loads((RAW / "role-penetration-audit.json").read_text(encoding="utf-8"))
    baseline = json.loads((RAW / "output-audit.json").read_text(encoding="utf-8"))
    result = {
        "status": "Candidate only. 120-240 second equal-ship combat NOT established.",
        "weapon_configurations": len(weapons["Rows"]),
        "guard_scenarios": len(weapons["Results"]),
        "single_shot_scenarios": len(hull["Depth"]),
        "battery_scenarios": len(hull["Results"]),
        "weapon_assumptions": weapons["Assumptions"],
        "hull_assumptions": hull["Assumptions"],
        "baseline_provenance": {k: v for k, v in baseline.items() if k.endswith("Sha256")},
        "candidate": weapons["Candidate"],
        "loadouts": weapons["Loadouts"],
        "guard": weapons["Results"],
        "native": weapons["Native"],
        "kinetic": weapons["Kinetic"],
        "electromagnetic": weapons["Electromagnetic"],
        "optional_charge_at_least_heads": weapons["OptionalChargeAtLeastHeads"],
        "depth": hull["Depth"],
        "hull": hull["Results"],
        "rail_energy": hull["Energy"],
        "source_hashes": {},
    }
    for path in ["scripts/WeaponTuningRegression/RoleBalanceAudit.cs",
                 "scripts/RailgunRegression/RolePenetrationAudit.cs",
                 "1.6/Assemblies/DeltaV.dll"]:
        result["source_hashes"][path] = hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
    # The complete 20,592-row scan remains in obj; retain all selected fixtures.
    for row in result["guard"]:
        row.pop("Trace", None)
    output = ROOT / "scripts/WeaponTuningRegression/results/2026-11-05-role-balance.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(output)
    for row in result["hull"]:
        if row["Name"] == "Current_Medium_Mixed":
            print(row["Name"], row["Pattern"], row["HalfDevicesLost"])


if __name__ == "__main__":
    main()
