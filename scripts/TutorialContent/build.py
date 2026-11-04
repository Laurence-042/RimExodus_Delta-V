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

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def build(data):
    defs, parents = {}, {}
    for path in [*data.glob('*/Defs/**/*.xml'), *(ROOT / '1.6/Defs').glob('**/*.xml')]:
        for node in ET.parse(path).getroot():
            if node.tag != 'ThingDef':
                continue
            if node.get('Name'):
                parents[node.get('Name')] = node
            if node.findtext('defName'):
                defs[node.findtext('defName')] = node

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

    width, height = 29, 25
    foundation = {(x, z) for x in range(width) for z in range(height)}
    records, occupied = [], {}
    def place(name, x, z, rot=0, stuff=None, layer='body'):
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
                      rotatable=prop(name, 'rotatable', 'true') == 'true',
                      interaction=pair(prop(name, 'interactionCellOffset', '(0,0,0)')),
                      hasInteraction=prop(name, 'hasInteractionCell', 'false') == 'true')
        records.append(record)
        if layer == 'body':
            occupied.update((c, record) for c in cells)
        return record

    # Three narrow bays keep every roof cell within native support distance.
    walls = {(x, z) for x, z in foundation if x in (0, 10, 19, 28) or z in (0, 24)}
    doors = {(10, 11), (19, 11), (14, 0)}
    for x, z in sorted(walls - doors):
        place('GravshipHull', x, z)
    for x, z in sorted(doors):
        place('Door', x, z)
    place('CryptosleepCasket', 5, 18)
    for x in (2, 8):
        place('Bed', x, 5)
    place('PilotConsole', 13, 19)
    place('Table2x2c', 13, 13)
    for x, z, rot in [(12, 13, 1), (15, 14, 3), (13, 12, 0), (14, 15, 2)]:
        place('DiningChair', x, z, rot)
    place('GravcorePowerCell', 24, 18)
    place('Battery', 24, 20)
    place('Battery', 26, 20)
    for x in (3, 14, 24):
        place('OxygenPump', x, 23)
        place('Heater', x, 3)
        place('StandingLamp', x, 8)
        place('StandingLamp', x, 16)
    # Explicit connected conductors, without overlapping the native transmitting cells.
    conductors = {(x, z) for x in (4, 14, 24) for z in range(1, 24)}
    conductors |= {(x, 10) for x in range(1, 28)} | {(x, 22) for x in range(24, 28)}
    transmitting = {tuple(c) for r in records if r['name'] in ('Battery', 'GravcorePowerCell') for c in r['cells']}
    for x, z in sorted(conductors - transmitting):
        place('HiddenConduit', x, z, layer='conduit')

    # Validate the real Def-derived interaction cells and routes, before either output.
    walkable = foundation - {p for p, r in occupied.items() if r['passability'] == 'Impassable' and r['name'] != 'Door'}
    reachable, queue = {(14, 11)}, deque([(14, 11)])
    while queue:
        x, z = queue.popleft()
        for n in ((x+1, z), (x-1, z), (x, z+1), (x, z-1)):
            if n in walkable and n not in reachable:
                reachable.add(n)
                queue.append(n)
    for r in records:
        if r['hasInteraction']:
            ix, _, iz = r['interaction']
            dx, dz = [(ix, iz), (iz, -ix), (-ix, -iz), (-iz, ix)][r['rotation']]
            p = (r['root'][0]+dx, r['root'][1]+dz)
            assert p in reachable, (r['name'], 'inaccessible interaction', p)
        if r['name'] == 'OxygenPump':
            assert (r['root'][0], r['root'][1]+1) in walls, 'pump must face its mounting wall'

    prototypes = {n.findtext('def'): n for n in ET.parse(HERE / 'prototypes.states').getroot()}
    document = ET.Element('MapPrefab', formatVersion='2')
    ET.SubElement(document, 'name').text = 'ArrivalStation'
    prefab = ET.SubElement(document, 'prefab')
    ET.SubElement(prefab, 'defName').text = 'MapPrefab_ArrivalStation'
    ET.SubElement(prefab, 'size').text = f'({width}, {height})'
    terrain = ET.SubElement(ET.SubElement(prefab, 'terrain'), 'MetalTile')
    ET.SubElement(ET.SubElement(terrain, 'rects'), 'li').text = f'(0,0,{width-1},{height-1})'
    things = ET.SubElement(document, 'thingStates')
    def field(node, key, value):
        found = node.find(key)
        if found is None:
            found = ET.SubElement(node, key)
        found.attrib.clear()
        found.text = str(value)
    for index, r in enumerate(records):
        state = copy.deepcopy(prototypes[r['name']])
        pos = f'({r["root"][0]}, 0, {r["root"][1]})'
        field(state, 'id', r['name'] + str(810000+index))
        field(state, 'pos', pos)
        field(state, 'rot', r['rotation'])
        entry = ET.SubElement(things, 'li')
        for key, value in [('pos', pos), ('def', r['name']), ('rotation', r['rotation']),
                           ('hitPoints', state.findtext('health')), ('stackCount', 1)]:
            field(entry, key, value)
        if r['stuff']:
            field(entry, 'stuff', r['stuff'])
            field(state, 'stuff', r['stuff'])
        ET.SubElement(entry, 'state').append(state)
    for tag, name in [('foundations', 'Substructure'), ('roofs', 'RoofConstructed')]:
        nodes = ET.SubElement(document, tag)
        for x, z in sorted(foundation):
            node = ET.SubElement(nodes, 'li')
            field(node, 'pos', f'({x}, 0, {z})')
            field(node, 'def', name)
    destination = ROOT / '1.6/TutorialPrefabs'
    destination.mkdir(exist_ok=True)
    ET.indent(document)
    ET.ElementTree(document).write(destination / 'ArrivalStation.xml', encoding='utf-8', xml_declaration=True)
    generated = ROOT / 'Source/obj/TutorialContent'
    generated.mkdir(parents=True, exist_ok=True)
    (generated / 'ArrivalStation.records.json').write_text(json.dumps(records, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    # Diagram reads these same resolved records; no second placement calculation.
    scale, pad = 25, 35
    image = Image.new('RGB', (width*scale+2*pad, height*scale+2*pad+90), '#101822')
    draw = ImageDraw.Draw(image)
    for x, z in sorted(foundation):
        draw.rectangle((pad+x*scale, pad+(height-1-z)*scale, pad+(x+1)*scale, pad+(height-z)*scale), fill='#354653', outline='#415563')
    colors = {'GravshipHull':'#798b91', 'Door':'#d7b66b', 'CryptosleepCasket':'#6edac8', 'Bed':'#8eb4e2', 'PilotConsole':'#f4aa55', 'Table2x2c':'#bb9b7a', 'GravcorePowerCell':'#dcdb74'}
    for r in records:
        if r['layer'] == 'conduit':
            continue
        xs, zs = zip(*r['cells'])
        box = (pad+min(xs)*scale+2, pad+(height-1-max(zs))*scale+2, pad+(max(xs)+1)*scale-2, pad+(height-min(zs))*scale-2)
        draw.rectangle(box, fill=colors.get(r['name'], '#abb6bb'))
        if r['name'] != 'GravshipHull':
            draw.text((box[0]+2, box[1]+2), r['name'][:2], fill='#101822')
    draw.text((pad, height*scale+pad+15), 'ARRIVAL STATION | 29 x 25 | North up | BUILT', fill='white')
    draw.text((pad, height*scale+pad+35), 'Left: cryosleep + beds   Center: briefing + bridge   Right: utilities', fill='white')
    counts = {name: sum(r['name'] == name for r in records) for name in ('CryptosleepCasket', 'Bed', 'OxygenPump', 'GravcorePowerCell')}
    draw.text((pad, height*scale+pad+55), f'{len(records)} instances | {counts["CryptosleepCasket"]} casket | {counts["Bed"]} beds | {counts["OxygenPump"]} oxygen pumps | {counts["GravcorePowerCell"]} gravcore cell', fill='white')
    image.save(HERE / 'ArrivalStation.png')
    # Re-read the output and verify no entity went missing during export.
    saved = ET.parse(destination / 'ArrivalStation.xml').getroot()
    assert len(saved.findall('thingStates/li')) == len(records)
    assert all(n.find('state/saveable') is not None for n in saved.findall('thingStates/li'))
    print(f'PASS: {len(records)} instances, {len(foundation)} foundation/roof cells; Def-derived geometry, routes and output counts.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', type=Path, default=Path('D:/SteamLibrary/steamapps/common/RimWorld/Data'))
    build(parser.parse_args().data)
