"""Author the shipped arrival station; one resolved layout feeds the prefab and diagram.

Run with the installed game Data directory. No game or Unity process is launched.
Scribe prototypes were extracted from local exports; the new empty casket follows
Building_Casket/ThingOwner.ExposeData. Native checks live in TutorialRegression.
"""
from pathlib import Path
import argparse
import copy
import json
import re
import xml.etree.ElementTree as ET
from collections import deque
from PIL import Image, ImageDraw
import shipyard_layout
import combat_range_layout
import arrival_layout
import sector_layout

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def validate_identities(document):
    ids = {}
    for state in document.findall('thingStates/li/state/saveable'):
        for node in state.iter():
            if node.find('def') is None or node.find('id') is None:
                continue
            load_id = node.findtext('id')
            number = int(re.search(r'\d+$', load_id).group())
            if number in ids:
                raise ValueError(f'Duplicate numeric Thing ID {number}: {ids[number]} / {load_id}')
            ids[number] = load_id
        for gun in state.findall('.//gun'):
            for index, verb in enumerate(gun.findall('verbTracker/verbs/li')):
                expected = f'CompEquippable_{gun.findtext("id")}_{index}'
                if verb.findtext('loadID') != expected:
                    raise ValueError(f'Gun/Verb identity mismatch: {gun.findtext("id")}')


def build(data, name="ArrivalStation"):
    if name == "ArrivalStation":
        return arrival_layout.build(data)
    defs, parents, terrains, terrain_parents = {}, {}, {}, {}
    colors, biomes = {}, {}
    for path in [*data.glob('*/Defs/**/*.xml'), *(ROOT / '1.6/Defs').glob('**/*.xml')]:
        for node in ET.parse(path).getroot():
            if node.tag == 'ColorDef': colors[node.findtext('defName')] = node
            if node.tag == 'BiomeDef': biomes[node.findtext('defName')] = node
            if node.tag not in ('ThingDef', 'TerrainDef'):
                continue
            definition_table, parent_table = (defs, parents) if node.tag == 'ThingDef' else (terrains, terrain_parents)
            if node.get('Name'):
                parent_table[node.get('Name')] = node
            if node.findtext('defName'):
                definition_table[node.findtext('defName')] = node

    def prop(name, path, default=None):
        node = defs[name]
        while node is not None:
            value = node.findtext(path)
            if value is not None:
                return value
            node = parents.get(node.get('ParentName'))
        return default

    def pair(value):
        return list(map(int, re.findall(r'-?\d+', value)))

    def comp_values(name, field):
        # Fields also occur on derived CompProperties (notably Battery). Respect
        # replacement comp lists instead of reviving a removed ancestor's comp.
        node = defs[name]
        while node is not None:
            comps = node.find('comps')
            if comps is not None:
                for comp in comps:
                    value = comp.findtext(field)
                    if value is not None:
                        yield value
                if comps.get('Inherit', 'true').lower() == 'false':
                    break
            node = parents.get(node.get('ParentName'))

    def affordances(name):
        node = terrains[name]
        result = []
        while node is not None:
            values = node.find('affordances')
            if values is not None:
                result.extend(v.text for v in values)
                if values.get('Inherit', 'True').lower() == 'false':
                    break
            node = terrain_parents.get(node.get('ParentName'))
        return list(dict.fromkeys(result))

    range_layout = name in ('WeaponRange', 'ShieldTarget')
    sector_content = name in sector_layout.NAMES
    detailed = range_layout or sector_content or name in ('Shipyard', 'PrototypeShip')
    layout = sector_layout if sector_content else combat_range_layout if range_layout else shipyard_layout
    plan = layout.shape(name) if detailed else None
    width, height = plan['size'] if detailed else {"TrainingShip": (21,15), "Salvage": (7,7)}[name]
    foundation = {(x, z) for x in range(width) for z in range(height)}
    if name == "TrainingShip": foundation = {c for c in foundation if c[0] >= 5}
    roofs = set(foundation) if name != "Salvage" else set()
    if name == "TrainingShip": roofs = {c for c in roofs if c[0] >= 5}
    if detailed:
        foundation = set(plan['cells'])
        roofs = set(foundation)
    records, occupied = [], {}
    def place(name, x, z, rot=0, stuff=None, layer='body', state=None):
        # ThingDef.ConfigErrors rejects numeric suffixes; Thing IDs append a number to the Def name.
        assert name and not name[-1].isnumeric(), (name, 'ThingDef name must not end in a number')
        size = pair(prop(name, 'size', '(1,1)'))
        w, h = size if rot % 2 == 0 else size[::-1]
        dx, dz = [(0, 0), (0, -1), (-1, -1), (-1, 0)][rot]
        root = [x + (w-1)//2 - (dx if w % 2 == 0 else 0),
                z + (h-1)//2 - (dz if h % 2 == 0 else 0)]
        cells = {(a, b) for a in range(x, x+w) for b in range(z, z+h)}
        assert cells <= foundation, (name, 'outside foundation')
        if layer == 'body':
            assert not cells & occupied.keys(), (name, 'overlap', cells & occupied.keys())
        if not stuff and int(prop(name, 'costStuffCount', '0')) > 0:
            stuff = 'Steel'
        record = dict(name=name, root=root, size=size, origin=[x, z], rotation=rot,
                      stuff=stuff, layer=layer, cells=sorted(cells),
                      passability=prop(name, 'passability', 'Standable'),
                      rotatable=prop(name, 'rotatable', 'true').lower() == 'true',
                      interaction=pair(prop(name, 'interactionCellOffset', '(0,0,0)')),
                      hasInteraction=prop(name, 'hasInteractionCell', 'false').lower() == 'true',
                      category=prop(name, 'category', 'Undefined'),
                      terrainAffordance=prop(name, 'terrainAffordanceNeeded'),
                      supportAffordances=affordances('OrbitalPlatform' if build_name == 'ArrivalStation' else 'Substructure'))
        record['stateOverrides'] = state or {}
        record['transmitsPower'] = any(v.lower() == 'true' for v in comp_values(name, 'transmitsPower'))
        record['powerWatts'] = sum(float(v) for v in comp_values(name, 'basePowerConsumption'))
        record['mass'] = float(prop(name, 'statBases/Mass', '4'))
        if stuff and prop(name, 'statBases/Mass') is not None:
            record['mass'] = record['mass'] * float(prop(stuff,'stuffProps/statFactors/Mass','1')) + float(prop(stuff,'stuffProps/statOffsets/Mass','0'))
        record['holdsRoof'] = prop(name, 'holdsRoof', 'false').lower() == 'true'
        record['airtight'] = prop(name, 'building/isAirtight', 'false').lower() == 'true' or name in ('GravshipHull', 'Wall', 'Door', 'DeltaV_DockingPort')
        exclusion = prop(name, "comps/li[@Class='CompProperties_GravshipThruster']/exclusionAreaSize")
        if exclusion:
            record['exclusionSize'] = pair(exclusion)
            record['exclusionOffset'] = pair(prop(name, "comps/li[@Class='CompProperties_GravshipThruster']/exclusionAreaOffset"))
        if layer == 'reservation':
            plan.setdefault('reservations', []).append(record)
            return record
        records.append(record)
        if layer == 'body':
            occupied.update((c, record) for c in cells)
        return record

    build_name = name
    if detailed:
        walls, start = layout.furnish(name, plan, place, prop, foundation, roofs, records, occupied)
    elif name == "Salvage":
        walls = set()
        place('ShelfSmall', 3, 3)
        place('Steel', 3, 3, layer='items')
    else:
        left = 5 if name == 'TrainingShip' else 0
        walls = {(x,z) for x,z in foundation if x in (left, width-1) or z in (0,height-1)}
        walls |= {(12 if name == 'TrainingShip' else 10,z) for z in range(height)}
        openings = {(left, 7 if name == 'TrainingShip' else 10)}
        openings.add((12,7) if name == 'TrainingShip' else (10,10))
        if name == 'TrainingShip': openings.add((20,7))
        if name == 'Shipyard': openings.add((20,10))
        engines = []
        if name == 'TrainingShip':
            # Short transverse arms on the main engines; only the stern has lateral RCS.
            for x in (10,14):
                engines += [place('LargeThruster', x, 0), place('LargeThruster', x, height-2, 2)]
            engines += [place('SmallThruster', left, 3, 1), place('SmallThruster', width-2, 3, 3)]
        elif name != 'Shipyard':
            for x in (left+3, width-4):
                engines += [place('SmallThruster', x, 0), place('SmallThruster', x, height-2, 2)]
            for z in (3,height-5):
                engines += [place('SmallThruster', left, z, 1), place('SmallThruster', width-2, z, 3)]
        engine_cells = {tuple(c) for r in engines for c in r['cells']}
        roofs -= engine_cells
        for x,z in sorted(walls - openings - engine_cells):place('GravshipHull',x,z)
        for x,z in sorted(openings):
            port = x == left or (name in ('Shipyard','TrainingShip') and x == 20)
            place('DeltaV_DockingPort' if port else 'Door',x,z,1 if x == 20 else 3)
        if name == 'TrainingShip':
            place('PilotConsole',7,10)
            place('ShelfSmall',16,7)
            place('CommsConsole',7,3)
            place('Bed',8,6)
            place('GravcorePowerCell',16,11)
            place('Battery',17,11)
            place('OxygenPump',9,13)
            place('OxygenPump',17,13)
            place('Heater',10,4)
            place('StandingLamp',14,7)
            start=(12,7)
        else:
            place('PilotConsole',3,15)
            place('Table2x2c',4,9)
            place('Bed',3,5)
            place('GravcorePowerCell',16,17)
            place('Battery',17,17)
            for x in (5,15):
                place('OxygenPump',x,19)
                place('Heater',x,3)
                place('StandingLamp',x,7)
            if name == 'PrototypeShip':
                place('GravEngine',13,10)
                place('ChemfuelTank',16,5)
            start=(5,11)
        # Connected service grid under the cabin. Engines stay clear of all exhaust cells.
        transmitting = {tuple(c) for r in records if r['name'] in ('Battery','GravcorePowerCell') for c in r['cells']}
        for x,z in sorted({(x,z) for x,z in foundation if x>left and x<width-1 and z>0 and z<height-1} - transmitting):
            place('HiddenConduit',x,z,layer='conduit')

    # Validate the real Def-derived interaction cells and routes, before either output.
    walkable = foundation - {p for p, r in occupied.items() if r['passability'] == 'Impassable' and r['name'] not in ('Door','DeltaV_DockingPort')}
    start = (3,2) if name == 'Salvage' else start
    reachable, queue = {start}, deque([start])
    while queue:
        x, z = queue.popleft()
        for n in ((x+1, z), (x-1, z), (x, z+1), (x, z-1)):
            if n in walkable and n not in reachable:
                reachable.add(n)
                queue.append(n)
    for r in records:
        if r['category'] == 'Building' and r['terrainAffordance']:
            assert r['terrainAffordance'] in r['supportAffordances'], (r['name'], 'unsupported terrain', r['terrainAffordance'])
        if r['hasInteraction']:
            ix, _, iz = r['interaction']
            dx, dz = [(ix, iz), (iz, -ix), (-ix, -iz), (-iz, ix)][r['rotation']]
            p = (r['root'][0]+dx, r['root'][1]+dz)
            assert p in reachable, (r['name'], 'inaccessible interaction', p)
        if r['name'] == 'OxygenPump':
            dx,dz = [(0,1),(1,0),(0,-1),(-1,0)][r['rotation']]
            assert (r['root'][0]+dx, r['root'][1]+dz) in walls, 'pump must face its mounting wall'
        if 'exclusionSize' in r:
            sx,_,sz = r['exclusionSize']
            ox,_,oz = r['exclusionOffset']
            exhaust = []
            for x in range(sx):
                for z in range(sz):
                    x0,z0 = x+ox,z+oz
                    dx,dz = [(x0,z0),(z0,-x0),(-x0,-z0),(-z0,x0)][r['rotation']]
                    exhaust.append((r['root'][0]+dx,r['root'][1]+dz))
            assert not foundation.intersection(exhaust), (r['name'], 'blocked exhaust')
            r['exclusionCells'] = exhaust

    prototypes = {n.findtext('def'): n for n in ET.parse(HERE / 'prototypes.states').getroot()}
    if name in ('ArrivalStation','Shipyard','PrototypeShip') or sector_content:
        prototypes.update({n.findtext('def'): n for n in ET.parse(HERE / 'shipyard.states').getroot()})
    if range_layout or sector_content:
        prototypes.update({n.findtext('def'): n for n in ET.parse(HERE / 'range.states').getroot()})
    document = ET.Element('MapPrefab', formatVersion='2')
    ET.SubElement(document, 'name').text = name
    prefab = ET.SubElement(document, 'prefab')
    ET.SubElement(prefab, 'defName').text = 'MapPrefab_' + name
    ET.SubElement(prefab, 'size').text = f'({width}, {height})'
    terrain = ET.SubElement(prefab, 'terrain')
    terrain_rects = {}
    for x,z in sorted(foundation):
        terrain_name = plan.get('surfaceDefs',{}).get((x,z),'MetalTile') if detailed else 'OrbitalPlatform' if name == 'ArrivalStation' else 'MetalTile'
        color = next((r['color'] for r in (plan or {}).get('rooms', []) if (x,z) in r['cells']), None)
        key = (terrain_name, color)
        if key not in terrain_rects:
            node = ET.SubElement(terrain, terrain_name)
            if color:
                assert color in colors, ('missing floor color', color)
                ET.SubElement(node, 'color').text = color
            terrain_rects[key] = ET.SubElement(node, 'rects')
        ET.SubElement(terrain_rects[key], 'li').text = f'({x},{z},{x},{z})'
    things = ET.SubElement(document, 'thingStates')
    def field(node, key, value):
        found = node.find(key)
        if found is None:
            found = ET.SubElement(node, key)
        found.attrib.clear()
        found.text = str(value)
    for index, r in enumerate(records):
        r['_loadId'] = r['name'] + str(810000 + index)
    for index, r in enumerate(records):
        state = copy.deepcopy(prototypes[r['name']])
        pos = f'({r["root"][0]}, 0, {r["root"][1]})'
        field(state, 'id', r['_loadId'])
        field(state, 'pos', pos)
        field(state, 'rot', r['rotation'])
        # Cloned turret internals also need distinct source identities before Scribe resolves them.
        renames = {}
        for child_index, node in enumerate(state.findall('.//id')):
            if node is state.find('id'): continue
            old = node.text
            renames[old] = re.sub(r'\d+$', '', old) + str(910000 + index*10 + child_index)
        for node in state.iter():
            if node.text:
                for old,new in renames.items(): node.text = node.text.replace(old,new)
        for key,value in r['stateOverrides'].items():
            if key.startswith('_'):
                continue
            if key == 'allowedDefs':
                settings = state.find('settings')
                if settings is not None: state.remove(settings)
                settings = ET.SubElement(state,'settings')
                field(settings,'priority','Preferred')
                filt = ET.SubElement(settings,'filter')
                ET.SubElement(filt,'disallowedSpecialFilters')
                allowed = ET.SubElement(filt,'allowedDefs')
                for item in value: ET.SubElement(allowed,'li').text=item
            else: field(state,key,value)
        if range_layout: combat_range_layout.prepare_state(r, state, field, prop)
        elif detailed: layout.prepare_state(r, state, field, prop)
        entry = ET.SubElement(things, 'li')
        for key, value in [('pos', pos), ('def', r['name']), ('rotation', r['rotation']),
                           ('hitPoints', state.findtext('health', '-1')), ('stackCount', state.findtext('stackCount', '1'))]:
            field(entry, key, value)
        if r['stuff']:
            field(entry, 'stuff', r['stuff'])
            field(state, 'stuff', r['stuff'])
        ET.SubElement(entry, 'state').append(state)
    for tag, surface_def in [('foundations', 'Substructure'), ('roofs', 'RoofConstructed')]:
        nodes = ET.SubElement(document, tag)
        foundation_output = set() if name == 'ArrivalStation' or (plan or {}).get('omitFoundations') else foundation
        for x, z in sorted(roofs if tag == 'roofs' else foundation_output):
            node = ET.SubElement(nodes, 'li')
            field(node, 'pos', f'({x}, 0, {z})')
            authored_defs = plan.get('foundationDefs' if tag == 'foundations' else 'roofDefs', {}) if detailed else {}
            field(node, 'def', authored_defs.get((x, z), surface_def))
    # Authored initial environment travels with the prefab, including imports outside the tutorial.
    # Include covered equipment cells too: impassable free-air regions share room state.
    # Solid wall samples are harmless; REF ignores cells with no target Room.
    environment = ET.SubElement(document, 'environment')
    if (plan or {}).get('reservations'):
        no_roof = ET.SubElement(document, 'noRoof')
        for x,z in sorted({tuple(c) for r in plan['reservations'] for c in r['cells']}):
            ET.SubElement(no_roof, 'li').text = f'({x}, 0, {z})'
    covered = roofs | {c for c, d in (plan or {}).get('foundationDefs', {}).items() if d == 'DeltaV_MoonPool'}
    environment_cells = [] if (plan or {}).get('omitEnvironment') else sorted(covered)
    environment_groups = {}
    for c in environment_cells:
        values = (plan or {}).get('environment', {}).get(c, (21, 0))
        environment_groups.setdefault(values, []).append(c)
    for (temperature, vacuum), samples in sorted(environment_groups.items()):
        entry = ET.SubElement(environment, 'li')
        field(entry, 'temperature', temperature)
        field(entry, 'vacuum', vacuum)
        cells = ET.SubElement(entry, 'cells')
        for x, z in samples:
            ET.SubElement(cells, 'li').text = f'({x}, 0, {z})'
    # Validate the final states after layout-specific edits, before replacing any outputs.
    validate_identities(document)
    generated = ROOT / 'Source/obj/TutorialContent'
    generated.mkdir(parents=True, exist_ok=True)
    (generated / (name + '.records.json')).write_text(json.dumps(records, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    if detailed:
        layout.validate_and_draw(name, plan, records, foundation, roofs, reachable, HERE, generated)
    if name in ('ArrivalStation', 'TrainingShip', 'Salvage', 'Shipyard', 'PrototypeShip', 'WeaponRange', 'ShieldTarget'):
        relative_destination = Path('Tutorial') / (name + '.xml')
    elif name in ('SectorFighter', 'SectorGunboat', 'SectorFrigate', 'SectorDestroyer', 'SectorCruiser'):
        relative_destination = Path('Sector/Craft') / (name + '.xml')
    elif name in ('SectorWeaponRelay', 'SectorAbandonedSatellite', 'SectorWeaponPlatform',
                  'SectorTradeStation', 'SectorShipyard', 'SectorFactoryStation'):
        relative_destination = Path('Sector/Stations') / (name + '.xml')
    elif name in ('SectorAsteroid', 'AsteroidSteelSmall', 'AsteroidSteelMedium',
                  'AsteroidComponentsSmall', 'AsteroidMixedMedium', 'AsteroidPlasteelLarge'):
        relative_destination = Path('Sector/Asteroids') / (name + '.xml')
    else:
        raise ValueError(f'No prefab catalog category for {name}')
    destination = ROOT / 'Common/Prefabs' / relative_destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(document)
    ET.ElementTree(document).write(destination, encoding='utf-8', xml_declaration=True)
    # Diagram reads these same resolved records; no second placement calculation.
    scale, pad = 25, 35
    image = Image.new('RGB', (width*scale+2*pad, height*scale+2*pad+90), '#101822')
    draw = ImageDraw.Draw(image)
    for x, z in sorted(foundation):
        draw.rectangle((pad+x*scale, pad+(height-1-z)*scale, pad+(x+1)*scale, pad+(height-z)*scale), fill='#354653', outline='#415563')
    colors = {'Wall':'#798b91', 'Ship_Beam':'#c99d67', 'Ship_Reactor':'#d9c96d', 'Ship_CryptosleepCasket':'#6edac8', 'CommsConsole':'#b1a0e0', 'GravshipHull':'#798b91', 'Door':'#d7b66b', 'CryptosleepCasket':'#6edac8', 'Bed':'#8eb4e2', 'PilotConsole':'#f4aa55', 'Table2x2c':'#bb9b7a', 'GravcorePowerCell':'#dcdb74'}
    for r in records:
        if r['layer'] == 'conduit':
            continue
        xs, zs = zip(*r['cells'])
        box = (pad+min(xs)*scale+2, pad+(height-1-max(zs))*scale+2, pad+(max(xs)+1)*scale-2, pad+(height-min(zs))*scale-2)
        draw.rectangle(box, fill=colors.get(r['name'], '#abb6bb'))
        if r['name'] != 'GravshipHull':
            draw.text((box[0]+2, box[1]+2), r['name'][:2], fill='#101822')
        if name == 'TrainingShip' and 'exclusionSize' in r:
            # Arrow indicates force, opposite to the cleared exhaust zone.
            cx,cy = (box[0]+box[2])/2, (box[1]+box[3])/2
            dx,dy = [(0,-1),(1,0),(0,1),(-1,0)][r['rotation']]
            tip = (cx+dx*8,cy+dy*8)
            draw.line((cx-dx*7,cy-dy*7,*tip), fill='#122b3c', width=2)
            draw.polygon([tip,(cx+dx*3-dy*4,cy+dy*3+dx*4),(cx+dx*3+dy*4,cy+dy*3-dx*4)],fill='#122b3c')
    draw.text((pad, height*scale+pad+15), f'{name} | {width} x {height} | North up | BUILT', fill='white')
    draw.text((pad, height*scale+pad+35), 'Resolved footprints; roofs use the same source.', fill='white')
    counts = {name: sum(r['name'] == name for r in records) for name in ('Ship_CryptosleepCasket' if name == 'ArrivalStation' else 'CryptosleepCasket', 'Bed', 'OxygenPump', 'GravcorePowerCell')}
    draw.text((pad, height*scale+pad+55), f'{len(records)} instances | {counts.get("Ship_CryptosleepCasket", counts.get("CryptosleepCasket", 0))} casket | {counts["Bed"]} beds | {counts["OxygenPump"]} oxygen pumps | {counts["GravcorePowerCell"]} gravcore cell', fill='white')
    if not detailed: image.save(HERE / (name + '.png'))
    # Re-read the output and verify no entity went missing during export.
    saved = ET.parse(destination).getroot()
    assert len(saved.findall('thingStates/li')) == len(records)
    assert all(n.find('state/saveable') is not None for n in saved.findall('thingStates/li'))
    assert len(saved.findall('environment/li/cells/li')) == len(environment_cells)
    support_count = 0 if (plan or {}).get('omitFoundations') or name == 'ArrivalStation' else len(foundation)
    print(f'PASS: {len(records)} instances, {support_count} support cells, {len(roofs)} roof cells; Def-derived geometry, routes and output counts.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', type=Path, default=Path('D:/SteamLibrary/steamapps/common/RimWorld/Data'))
    parser.add_argument('--only', nargs='+', choices=['ArrivalStation','TrainingShip','Salvage','Shipyard','PrototypeShip','WeaponRange','ShieldTarget', *sector_layout.NAMES])
    args = parser.parse_args()
    for name in args.only or ('ArrivalStation','TrainingShip','Salvage','Shipyard','PrototypeShip','WeaponRange','ShieldTarget'): build(args.data, name)
