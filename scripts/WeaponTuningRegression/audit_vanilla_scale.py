"""Compare native charge-blaster scale with the production-component output audit.

This is a capacity estimate, not a pawn AI or hit-probability simulation. Supply is
unlimited; thermal cycles are read from OutputAudit, never reimplemented here.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--game-data', type=Path,
                    default=Path('D:/SteamLibrary/steamapps/common/RimWorld/Data'))
args = parser.parse_args()
source = args.game_data / 'Core/Defs/ThingDefs_Misc/Weapons/RangedMechanoid.xml'
defs = ET.parse(source).getroot()
base = defs.find("ThingDef[@Name='Gun_ChargeBlasterHeavyBase']")
gun = defs.find("ThingDef[defName='Gun_ChargeBlasterHeavy']")
bullet = defs.find("ThingDef[defName='Bullet_ChargeBlasterHeavy']")
verb = gun.find('verbs/li')
damage = float(bullet.findtext('projectile/damageAmountBase'))
shots = int(verb.findtext('burstShotCount'))
interval = int(verb.findtext('ticksBetweenBurstShots'))
warmup = float(verb.findtext('warmupTime'))
cooldown = float(base.findtext('statBases/RangedWeapon_Cooldown'))
# VerbProperties.AdjustedFullCycleTime; unmodified pawn aiming/cooldown factors.
cycle = warmup + (shots - 1) * interval / 60 + cooldown
capacities = []
for capacity in (1000, 3000, 30000, 36000):
    hits = math.ceil(capacity / damage)
    burst, index = divmod(hits - 1, shots)
    seconds = burst * cycle + index * interval / 60
    capacities.append(dict(capacity=capacity, hits=hits, bursts=burst + 1,
                           seconds_from_first_hit=seconds))

audit = json.loads((ROOT / 'Source/obj/WeaponTuningRegression/output-audit.json').read_text())
for field, relative in (
    ('DefsSha256', '1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml'),
    ('EffectsSha256', '1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml'),
):
    if hashlib.sha256((ROOT / relative).read_bytes()).hexdigest().upper() != audit[field]:
        raise SystemExit('Stale output audit; rerun run.ps1 -OutputAudit')
thermal = []
for row in audit['Rows']:
    if row['Size'] != 'Medium' or row['Material'] == 'Uranium':
        continue
    family = row['Family']
    selected = (family == 'Rapid' and row['Interval'] in (4, 10, 20, 25)
                or family == 'Heavy' and (row['Charge'], row['Heads']) in
                ((1, 96), (8, 96), (48, 48), (96, 96))
                or family == 'Laser' and row['Power'] in (1, 4))
    if not selected:
        continue
    lock = row['FirstLockSeconds']
    thermal.append(dict(key=row['Key'], overheats=lock is not None,
                        firing_seconds_to_lock=lock,
                        cooling_seconds=None if lock is None else row['CycleTicks'] / 60 - lock,
                        thermal_cycle_seconds=None if lock is None else row['CycleTicks'] / 60,
                        shots_to_lock=None if lock is None else row['CycleShots'],
                        damage_to_lock=None if lock is None else row['CycleShots'] * row['Damage'],
                        sustained_dps=row['SustainedDps']))
result = dict(
    assumptions='60 ticks/s; all projectiles intercepted; native unmodified pawn factors; '
    'capacity timing excludes all shield regeneration and first aim/flight delay; '
    'unlimited ammunition and power; no reload-defined rounds; '
    'no thermal round assigned to weapons that never overheat.',
    native_source=str(source), native_sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
    native=dict(damage=damage, burst_shots=shots, shot_interval_ticks=interval,
                warmup_seconds=warmup, cooldown_seconds=cooldown,
                cycle_seconds=cycle, burst_damage=damage * shots,
                sustained_dps=damage * shots / cycle),
    capacities=capacities, thermal=thermal)
output = ROOT / 'Source/obj/WeaponTuningRegression/vanilla-scale-audit.json'
output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(result, ensure_ascii=False, indent=2))
print(output)
