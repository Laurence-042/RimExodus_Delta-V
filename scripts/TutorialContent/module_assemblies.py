"""Trial ships assembled from the reviewed pressure modules."""
from pathlib import Path
import json
import math

from PIL import Image, ImageDraw, ImageFont

from module_layout import (TITLES, adjacent, flood, furnish as furnish_module, prepare_state,
                           rect, seam, shape as module_shape)


NAMES = ('AssemblyCombat', 'AssemblyIndustrial')
COMMON_BRIDGE = (3, 13, 9, 20)
DESIGNS = {
    'AssemblyCombat': dict(
        modules=[('ModuleCasemate', 10, 18, 0), ('ModuleMissile', 16, 18, 0),
                 ('ModulePointDefense', 22, 18, 0), ('ModuleRailgun', 28, 18, 0),
                 ('ModuleHeavyBattery', 9, 5, 2), ('ModuleBedroom', 15, 9, 2),
                 ('ModuleGuard', 21, 10, 2), ('ModuleBedroom', 3, 8, 2), ('ModuleReactor', 27, 5, 2)],
        corridor=(10, 15, 34, 17), port=(35, 16),
        open_joins=[], shared_joins=[(0, 1), (1, 2), (2, 3), (7, 4), (4, 5), (5, 6), (6, 8)],
        engines={0: [(x, 4) for x in (9, 11, 13, 27, 29, 31)] + [(5, 7), (24, 9)],
                 2: [(4, 20), (6, 20), (23, 22), (25, 22)],
                 1: [(2, 8), (2, 11), (2, 18), (27, 26)],
                 3: [(33, 5), (33, 7), (34, 19), (34, 22)]},
        armor=rect(2, 15, 2, 17) | rect(16, 5, 16, 7) | rect(34, 9, 34, 12)),
    'AssemblyIndustrial': dict(
        modules=[('ModuleBridge', 3, 18, 0), ('ModuleResearch', 13, 18, 0), ('ModuleFabrication', 18, 18, 0),
                 ('ModuleCargo', 24, 18, 0), ('ModuleMachining', 24, 22, 0),
                 ('ModuleFuel', 3, 5, 2), ('ModuleCargo', 9, 9, 2), ('ModuleBedroom', 15, 9, 2),
                 ('ModuleGenerator', 21, 9, 2)],
        corridor=(3, 15, 30, 17), port=(31, 16),
        open_joins=[(1, 2, 0), (3, 4, 1)], shared_joins=[(0, 1), (2, 3), (2, 4), (5, 6), (6, 7), (7, 8)],
        engines={0: [(x, 1) for x in (9, 11, 13)] + [(22, 8), (25, 8)],
                 2: [(6, 25), (10, 25), (18, 23), (26, 27)],
                 1: [(2, 8), (2, 12), (2, 22)],
                 3: [(15, 4), (27, 10), (30, 21), (30, 24)]},
        armor=set(), pump_anchors=(4, 9, 14, 20, 26)),
}


def translated(cells, width, depth, x, z, rotation):
    if rotation == 2:
        return {(x + width - 1 - a, z + depth - 1 - b) for a, b in cells}
    return {(x + a, z + b) for a, b in cells}


def shape(name, footprint, prop, exhaust):
    spec = DESIGNS[name]
    corridor = rect(*spec['corridor'])
    # Orthogonal boundary only. Door and vent faces are supplied by the reviewed modules.
    rooms = {'驾驶与指挥': rect(*COMMON_BRIDGE) | rect(10, 18, 10, 20)} if name == 'AssemblyCombat' else {'餐厨': rect(9, 2, 15, 9)}
    room_walls = set().union(*(cells & adjacent(adjacent(cells) - cells) for cells in rooms.values()))
    room_cells = set().union(*rooms.values())
    body = corridor | room_cells
    cuts, shared_cells, open_seams = {}, {}, []
    for left, right, axis in spec['open_joins']:
        a, ax, az, ar = spec['modules'][left]
        b, bx, bz, br = spec['modules'][right]
        assert ar == br == 0
        shift, edge_a, edge_b = seam(module_shape(a)['edges'], module_shape(b)['edges'], axis, False)
        assert (bx - ax, bz - az) == shift
        cuts.setdefault(left, set()).update(edge_a)
        cuts.setdefault(right, set()).update(edge_b)
        open_seams.append((axis, {(x + ax, z + az) for x, z in edge_a}))
    modules = []
    shells = []
    exterior_faces = []
    for index, (module, x, z, rotation) in enumerate(spec['modules']):
        local = module_shape(module)
        cut = cuts.get(index, set())
        cells = translated(local['cells'] - cut, *local['size'], x, z, rotation)
        modules.append((module, x, z, rotation, local, cut, cells))
        shell = translated(local['shell'] - cut, *local['size'], x, z, rotation)
        shells.append(shell)
        exterior = set().union(*(set(edge['cells']) for edge in local['edges'].values() if edge['kind'] == 'exterior'))
        exterior = translated(exterior, *local['size'], x, z, rotation)
        exterior_faces.append({(a, b) for a, b in exterior if {(a-1, b), (a+1, b)} <= exterior})
        overlap = cells & room_cells
        assert not cells & corridor
        assert overlap <= room_walls & shell - exterior, (name, module, 'room crosses module wall')
        for cell in overlap:
            shared_cells[cell] = 1 if (cell[0], cell[1] - 1) in shell & room_walls or (
                cell[0], cell[1] + 1) in shell & room_walls else 0
        body |= cells
    for left, (module, *_, a) in enumerate(modules):
        for right in range(left + 1, len(modules)):
            overlap = a & modules[right][-1]
            if (left, right) not in spec['shared_joins'] and (right, left) not in spec['shared_joins']:
                assert not overlap, (name, module, modules[right][0], 'unexpected overlap', overlap)
                continue
            assert overlap and overlap <= shells[left] & shells[right]
            assert len({x for x, _ in overlap}) == 1
            assert not overlap & (exterior_faces[left] | exterior_faces[right])
            shared_cells.update(dict.fromkeys(overlap, 1))
    hull = body | adjacent(corridor)
    plan = dict(spec=spec, corridor=corridor, roomsByName=rooms, modules=modules,
                ports={spec['port']: 1}, armor=spec['armor'], sharedCells=shared_cells,
                openSeams=open_seams, roomWalls=room_walls, hull=hull, noRoofCells=set())
    fixtures = module_fixtures(name, plan, footprint, prop)
    plan['fixtures'] = fixtures
    engines = [(x, z, rotation) for rotation, origins in spec['engines'].items() for x, z in origins]
    engine_cells = set().union(*(footprint('LargeThruster', *e)[2] for e in engines))
    foundation = hull | spec['armor'] | engine_cells
    assert flood(min(foundation), foundation) == foundation, (name, 'detached structure')
    plan.update(size=(max(x for x, _ in foundation) + 1, max(z for _, z in foundation) + 1),
                cells=foundation, engines=engines)
    plan['noRoofCells'].update(engine_cells)
    return plan


def module_fixtures(name, plan, footprint, prop):
    result = {}
    for module, x, z, rotation, local, cut, _ in plan['modules']:
        fixtures, positions = [], {}

        def collect(thing, a, b, rot=0, stuff=None, layer='body', state=None):
            if layer != 'body':
                return
            _, _, cells = footprint(thing, a, b, rot)
            fixture = dict(cells=cells, name=thing, transmitsPower=False)
            positions.update((cell, fixture) for cell in cells)
            fixtures.append((thing, cells, rot, stuff, state))
            return fixture

        furnish_module(module, local, collect, footprint, prop, local['cells'], set(local['cells']), [], positions)
        plan['noRoofCells'].update(translated(local.get('noRoofCells', set()), *local['size'], x, z, rotation))
        for thing, cells, rot, stuff, state in fixtures:
            if cells & cut:
                assert cells <= cut and thing in ('GravshipHull', 'Door', 'Vent')
                continue
            cells = translated(cells, *local['size'], x, z, rotation)
            rot = (rot + rotation) % 4 if prop(thing, 'rotatable', 'true').lower() == 'true' else 0
            a, b = min(c[0] for c in cells), min(c[1] for c in cells)
            assert footprint(thing, a, b, rot)[2] == cells, (name, module, thing, 'rotated footprint')
            if thing == 'Vent':
                state = {'switchOn': 'True', 'wantSwitchOn': 'True'}
            apertures = {(12, 9): 'Door', (13, 9): 'Vent'} if name == 'AssemblyIndustrial' else {}
            if module == 'ModuleCargo' and thing == 'GravshipHull' and (a, b) in apertures:
                thing = apertures[a, b]
                rot = 0
                state = {'switchOn': 'True', 'wantSwitchOn': 'True'} if thing == 'Vent' else None
            for cell in cells & result.keys():
                prior = result[cell][0]
                assert cell in plan['sharedCells'] and 'GravshipHull' in (prior, thing) and (
                    {prior, thing} <= {'GravshipHull', 'Door', 'Vent'}
                    or {prior, thing} == {'GravshipHull', 'DeltaV_Weapon_Casemate'}), (
                    name, module, cell, prior, thing)
            fixture = thing, a, b, rot, stuff, state
            for cell in cells:
                if cell not in result or thing != 'GravshipHull':
                    result[cell] = fixture
    return result


def furnish(name, plan, place, footprint, prop, foundation, roofs, records, occupied):
    spec = plan['spec']
    module_fixtures = plan['fixtures']
    wall_cells = (adjacent(plan['corridor']) - plan['corridor']) | plan['roomWalls'] | plan['armor']
    reserved = {(9, 16), (9, 17)} if name == 'AssemblyCombat' else set()
    wall_cells -= reserved | {spec['port']}
    for cell, fixture in module_fixtures.items():
        thing, a, b, rot, stuff, state = fixture
        if thing != 'GravshipHull' and cell == (a, b):
            place(thing, a, b, rot, stuff=stuff, state=state)
    wall_cells.update(c for c, f in module_fixtures.items() if f[0] == 'GravshipHull')
    wall_cells -= occupied.keys()
    for engine in plan['engines']:
        record = place('LargeThruster', *engine, state={'DeltaV_powerPercent': 100})
        assert record['airtight'] and record['holdsRoof'] and record['passability'] == 'Impassable'
        cells = set(record['cells'])
        dx, dz = ((0, 1), (1, 0), (0, -1), (-1, 0))[record['rotation']]
        face = {c for c in cells if (c[0] + dx, c[1] + dz) not in cells}
        assert face <= wall_cells & plan['hull'] and not (cells - face) & plan['hull'], (
            name, 'thruster must replace the exterior pressure wall', engine)
        record['replacedWallCells'] = sorted(face)
    roofs.difference_update(plan['noRoofCells'])
    port = spec['port']
    place('DeltaV_DockingPort', *port, 1)
    if name == 'AssemblyCombat':
        place('Door', 9, 16)
        place('Vent', 9, 17, 1, state={'switchOn': 'True', 'wantSwitchOn': 'True'})
        place('PilotConsole', 4, 15)
        place('DeltaV_ShieldGenerator', 4, 17)
        place('ChemfuelPoweredGenerator', 7, 18, state={'fuel': 30, 'configuredTargetFuelLevel': 30})
        for z in (15, 17):
            place('GravshipHull', 21, z)
        place('Door', 21, 16)
        crew = [(14, 16), (20, 16), (27, 16), (34, 16)]
    else:
        for thing, x, z, rot, state in (
            ('DeltaV_Weapon_PDTurret', 10, 7, 0, None), ('ElectricStove', 10, 5, 2, None),
            ('Table2x2c', 13, 3, 0, None), ('DiningChair', 13, 6, 2, None),
            ('DiningChair', 14, 6, 2, None), ('ShelfSmall', 11, 7, 0, {'allowedDefs': ['MealSurvivalPack']})):
            place(thing, x, z, rot, state=state)
        crew = [(14, 16), (21, 16)]
    for cell in crew:
        place('DeltaV_PrefabPawnMarker', *cell, layer='marker')
    for cell in sorted(wall_cells - occupied.keys()):
        place('GravshipHull', *cell, stuff='Plasteel' if cell in plan['armor'] else 'Steel')
    walls = {cell for cell, r in occupied.items() if r['name'] in ('GravshipHull', 'LargeThruster')}
    west = spec['corridor'][0]
    south, north = spec['corridor'][1], spec['corridor'][3]
    for x in spec.get('pump_anchors', range(west + 1, spec['corridor'][2], 5)):
        for z, rotation in [(north, 0), (south, 2)]:
            dx, dz = ((0, 1), (1, 0), (0, -1), (-1, 0))[rotation]
            if (x + dx, z + dz) in walls and (x, z) not in occupied:
                place('OxygenPump', x, z, rotation)
                break
    for anchor in range(west + 2, spec['corridor'][2], 4):
        x, z = next((x, z) for x in sorted(range(west, spec['corridor'][2] + 1), key=lambda x: abs(x - anchor))
                    for z in (south, north) if (x, z) not in occupied and (
                        x, south - 1 if z == south else north + 1) in walls)
        place('Heater', x, z, state={'targetTemperature': 21})
    for r in records:
        if r['name'] == 'DeltaV_ShieldGenerator':
            x, z = r['root']
            radius = math.ceil(max(math.hypot(a - x + dx, b - z + dz) for a, b in foundation
                                   for dx in (-.5, .5) for dz in (-.5, .5)))
            assert radius <= int(prop(r['name'], "comps/li[@Class='DeltaV.CompProperties_SpaceShield']/maxGroundRadius", '40'))
            r['stateOverrides']['DeltaV_shieldGroundRadius'] = radius
    transmitters = {c for c, r in occupied.items() if r['transmitsPower']}
    for cell in sorted(foundation - transmitters):
        place('HiddenConduit', *cell, layer='conduit')
    pilot = next(r for r in records if r['name'] == 'PilotConsole')
    return walls, (pilot['root'][0], pilot['root'][1] - 1)


def validate(name, plan, records, foundation, roofs, reachable):
    bodies = {c: r for r in records if r['layer'] == 'body' for c in r['cells']}
    assert all(bodies[c]['airtight'] or bodies[c]['name'] == 'Vent' for c in plan['sharedCells'])
    clear = roofs - {c for c, r in bodies.items() if r['passability'] != 'Standable'}
    for axis, cells in plan['openSeams']:
        dx, dz = (1, 0) if axis == 0 else (0, 1)
        assert any((x, z) in clear and (x - dx, z - dz) in clear for x, z in cells), (
            name, 'no clear passage at removed partition', cells)
    airtight = {c for c, r in bodies.items() if r['airtight']}
    air = roofs - airtight
    leaks = adjacent(air) - roofs - airtight
    assert not leaks, (name, 'open pressure boundary', leaks)
    steel = {c for c, r in bodies.items() if r['name'] == 'GravshipHull' and r['stuff'] == 'Steel'}
    for dx, dz in ((1, 0), (0, 1)):
        doubled = {(x + dx, z + dz) for x, z in air if (x + dx, z + dz) in steel
                   and (x + 2*dx, z + 2*dz) in steel and (x + 3*dx, z + 3*dz) in air}
        assert not doubled, (name, 'two steel walls between enclosed rooms', doubled)
    for x, z in (c for c, r in bodies.items() if r['name'] == 'Door'):
        assert any({(x - dx, z - dz), (x + dx, z + dz)} <= clear & reachable for dx, dz in ((1, 0), (0, 1))), (
            name, 'interior door must have a clear approach from both sides', (x, z))
    for r in records:
        if 'replacedWallCells' not in r:
            continue
        dx, dz = ((0, 1), (1, 0), (0, -1), (-1, 0))[r['rotation']]
        assert any((x + dx, z + dz) in air & reachable for x, z in r['replacedWallCells']), (
            name, 'no interior maintenance access behind thruster', r['origin'])
        assert not roofs.intersection(r['exclusionCells'])
    vent_cells = {c for c, r in bodies.items() if r['name'] == 'Vent'}
    for c in vent_cells:
        r = bodies[c]
        dx, dz = ((0, 1), (1, 0), (0, -1), (-1, 0))[r['rotation']]
        assert all((c[0] + sign*dx, c[1] + sign*dz) in air for sign in (-1, 1)), (
            name, 'vent must connect two enclosed rooms', c)
    remaining = air | vent_cells
    zone_count = 0
    while remaining:
        zone = flood(min(remaining), remaining)
        remaining -= zone
        for device in ('Heater', 'OxygenPump'):
            assert any(bodies.get(c, {}).get('name') == device for c in zone), (name, 'unsupported air zone', device)
        zone_count += 1
    assert zone_count == (2 if name == 'AssemblyCombat' else 1)
    assert all(adjacent(set(r['cells'])) & reachable for r in records
               if r['name'] in ('Vent', 'Shelf', 'ShelfSmall', 'DeltaV_CargoStorage', 'DeltaV_ShipReactor',
                                'DeltaV_Building_MissileMagazine'))
    for r in records:
        if r['name'] in ('DeltaV_Weapon_Casemate', 'DeltaV_Weapon_MissileLauncher'):
            x, z = r['root']
            front = max(b for _, b in r['cells'])
            assert not any(a == x and b > front for a, b in foundation), (name, 'blocked fixed weapon', r)
    available = -sum(min(0, r['powerWatts']) for r in records)
    load = sum(max(0, r['powerWatts']) for r in records)
    recharge = 1000 * sum(r['name'] == 'DeltaV_ShieldGenerator' for r in records)
    assert available >= load + recharge, (
        name, 'insufficient generation', load, available)
    report = dict(name=name, foundation=sorted(foundation), roofs=sorted(roofs), airZones=zone_count,
                  generationW=available, nameplateLoadW=load, rechargeW=recharge)
    return report, bodies


def validate_and_draw(name, plan, records, foundation, roofs, reachable, here, generated):
    report, bodies = validate(name, plan, records, foundation, roofs, reachable)
    write_preview(name, plan, report, bodies, generated)


def write_preview(name, plan, report, bodies, generated):
    (generated / f'{name}.layout.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    draw_ship(name, plan, bodies, generated / f'{name}.png')
    print(f"{name}: {len(report['foundation'])} foundation, {report['airZones']} air zones, "
          f"{report['nameplateLoadW']:g} W load / {report['generationW']:g} W generation")


def draw_ship(name, plan, bodies, path):
    scale, margin = 22, 24
    width, height = plan['size']
    image = Image.new('RGB', (width * scale + 2 * margin, height * scale + 108), '#101a28')
    draw = ImageDraw.Draw(image)
    font_path = Path('C:/Windows/Fonts/msyh.ttc')
    title = ImageFont.truetype(str(font_path), 24)
    small = ImageFont.truetype(str(font_path), 13)
    swatches = ('#386a67', '#505e86', '#705275', '#7b624e')
    regions = {}
    for i, (_, _, _, _, _, _, cells) in enumerate(plan['modules']):
        regions.update({c: swatches[i % len(swatches)] for c in cells})
    for i, cells in enumerate(plan['roomsByName'].values()):
        regions.update({c: swatches[(i + 1) % len(swatches)] for c in cells})
    for x, z in plan['cells']:
        r = bodies.get((x, z))
        color = regions.get((x, z), '#2a4257')
        if r:
            if r['name'] == 'GravshipHull':
                color = '#a5a0bb' if r['stuff'] == 'Plasteel' else '#607284'
            elif r['name'] == 'Vent':
                color = '#7cd5ef'
            elif r['name'] == 'DeltaV_VacuumBarrier':
                color = '#8adbf7'
            elif r['name'] in ('Door', 'DeltaV_DockingPort'):
                color = '#e0b873'
            elif r['name'].endswith('Thruster'):
                color = '#f2a579' if (x, z) in r['replacedWallCells'] else '#b66249'
            elif r['name'] == 'OxygenPump' or r['name'] == 'Heater':
                color = '#79c9b0'
            else:
                color = '#9aabbc'
        a, b = margin + x * scale, 58 + (height - 1 - z) * scale
        draw.rectangle((a, b, a + scale - 1, b + scale - 1), fill=color, outline='#182839')
        axis = plan['sharedCells'].get((x, z))
        if axis is not None:
            center = scale // 2
            line = (a + center, b, a + center, b + scale) if axis == 1 else (a, b + center, a + scale, b + center)
            draw.line(line, fill='#e3afeb', width=3)
    for axis, cells in plan['openSeams']:
        for x, z in cells:
            a, b = margin + x * scale, 58 + (height - 1 - z) * scale
            line = (a, b + 3, a, b + scale - 3) if axis == 0 else (a + 3, b + scale, a + scale - 3, b + scale)
            draw.line(line, fill='#6de0b9', width=2)
    labels = [(TITLES[module], cells) for module, _, _, _, _, _, cells in plan['modules']]
    labels.extend(plan['roomsByName'].items())
    for label, cells in labels:
        x = (min(a for a, _ in cells) + max(a for a, _ in cells) + 1) / 2
        z = (min(b for _, b in cells) + max(b for _, b in cells) + 1) / 2
        draw.text((margin + x*scale, 58 + (height - z)*scale), label, font=small, anchor='mm',
                  fill='white', stroke_width=2, stroke_fill='#152334')
    draw.text((margin, 15), plan['spec'].get('title', '交战型试装' if name == 'AssemblyCombat' else '生产运输型试装'),
              font=title, fill='white')
    draw.text((margin, height*scale+69), '北朝上 · 绿线为拆墙 · 紫线为共墙 · 亮橙为推进器替换的舱壁格',
              font=small, fill='#cad7e9')
    image.save(path)
