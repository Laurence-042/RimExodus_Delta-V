"""Read current prefab and installed Core/Odyssey + Delta-V Defs; write probe inputs only."""
import hashlib
import importlib.util
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('fighter_defs', ROOT/'Prefabs/_design/Fighter_0/prefab_defs.py')
defs_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(defs_module)
defs_module.REF = Path('D:/SteamLibrary/steamapps/common/RimWorld/Data')
defs = defs_module.Defs()
source = ROOT/'Prefabs/Fighter_0.xml'
prefab = ET.parse(source).getroot()
out = ET.Element('Fighter', sha256=hashlib.sha256(source.read_bytes()).hexdigest(), size=prefab.findtext('prefab/size'))
out.append(prefab.find('foundations'))
counts = {}
for entry in prefab.findall('thingStates/li'):
    name = entry.findtext('def')
    if defs.prop(name, 'category') == 'Item' or name == 'DeltaV_PrefabPawnMarker':
        continue
    mass = float(defs.prop(name, 'statBases/Mass', '4'))
    stuff = entry.findtext('stuff')
    if stuff and defs.prop(name, 'statBases/Mass') is not None:
        mass += float(defs.prop(stuff, 'stuffProps/statOffsets/Mass', '0'))
        mass *= float(defs.prop(stuff, 'stuffProps/statFactors/Mass', '1'))
    # This prefab has no transporter; loose inventory/pawns do not enter current ShipPhysics.Measure.
    assert not any(c.get('Class') == 'CompProperties_Transporter' for c in defs.comps(name))
    counts[name] = counts.get(name, 0) + 1
    ET.SubElement(out, 'building', name=name, pos=entry.findtext('pos'),
                  rotation=entry.findtext('rotation', '0'), size=defs.prop(name, 'size', '(1,1)'),
                  mass=str(mass), power=entry.findtext('state/saveable/DeltaV_powerPercent', '100'))
dest = ROOT/'Source/obj/CombatManeuverSimulation/fighter-inputs.xml'
dest.parent.mkdir(parents=True, exist_ok=True)
ET.ElementTree(out).write(dest, encoding='utf-8', xml_declaration=True)
print(json.dumps({'sha256':out.get('sha256'), 'buildings':counts, 'output':str(dest)}, indent=2))
