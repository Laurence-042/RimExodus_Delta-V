"""Independent pressure compartments. All placements are footprint southwest cells."""
from collections import deque
from pathlib import Path
import json

from PIL import Image, ImageDraw, ImageFont

import sector_layout


def rect(x0, z0, x1, z1):
    return {(x, z) for x in range(x0, x1 + 1) for z in range(z0, z1 + 1)}


def compartment(width, depth, fixtures, *, entrances=(3,), vents=(4,), divider=None,
                open_edges=(), exterior_edges=(), front_layers=1, rail=0,
                removable_sides=True):
    doors, vents = {(x, 0) for x in entrances}, {(x, 0) for x in vents}
    sides = ('west', 'east') if removable_sides else ()
    return dict(roomSize=(width, depth), doors=doors, vents=vents, divider=divider,
                fixtures=fixtures, openEdges=open_edges + sides, exteriorEdges=exterior_edges,
                frontLayers=front_layers, rail=rail)


def gun_bay(def_name, supplies):
    return compartment(7, 10, [
        (def_name, 3, 2, 1), (def_name, 3, 6, 1), *supplies,
    ])


ammo = {'allowedDefs': ['DeltaV_SteelHead', 'DeltaV_UraniumHead', 'DeltaV_Propellant']}
ammo_shelves = [('Shelf', 1, z, 1, ammo) for z in (2, 6)]
battery_capacity = {
    'Battery': "comps/li[@Class='CompProperties_Battery']/storedEnergyMax",
    'DeltaV_SafetyBattery': "comps/li[@Class='DeltaV.CompProperties_SwitchableBattery']/storedEnergyMax",
}
MODULES = {
    'ModuleResearch': compartment(7, 6, [
        ('HiTechResearchBench', 1, 3), ('MultiAnalyzer', 1, 1),
        ('StandingLamp', 4, 2),
    ], open_edges=('south',)),
    'ModuleMachining': compartment(7, 6, [
        ('TableMachining', 1, 2, 3), ('TableMachining', 5, 2, 1),
        ('Shelf', 1, 1, 0, {'allowedDefs': ['Steel', 'Uranium']}),
        ('ShelfSmall', 2, 4, 0, ammo), ('ShelfSmall', 5, 1, 0, ammo), ('StandingLamp', 4, 4),
    ], open_edges=('south',)),
    'ModuleFabrication': compartment(7, 6, [
        ('FabricationBench', 1, 3), ('ToolCabinet', 4, 2),
        ('Shelf', 1, 1, 0, {'allowedDefs': ['Steel', 'Plasteel', 'Gold', 'ComponentIndustrial', 'ComponentSpacer']}),
        ('StandingLamp', 5, 1),
    ], open_edges=('south',)),
    'ModuleBedroom': compartment(7, 6, [
        ('Bed', 1, 2, 2), ('EndTable', 1, 4),
        ('Bed', 5, 2, 2), ('EndTable', 5, 4),
    ], entrances=(2, 4), vents=(1, 5), divider=3),
    'ModuleCargo': compartment(7, 6, [
        ('DeltaV_CargoStorage', 1, 2), ('DeltaV_CargoStorage', 4, 2),
    ], open_edges=('north',)),
    'ModuleHeavyBattery': gun_bay('DeltaV_Weapon_HeavyMainMedium', ammo_shelves),
    'ModuleRapidBattery': gun_bay('DeltaV_Weapon_RapidMainMedium', ammo_shelves),
    'ModuleLaserBattery': gun_bay('DeltaV_Weapon_LaserMainMedium', [('Battery', 1, z) for z in (2, 6)]),
    'ModulePointDefense': compartment(7, 5, [
        *[('DeltaV_Weapon_PDTurret', x, z) for z in (1, 3) for x in (4, 5)],
        ('DeltaV_SafetyBattery', 1, 1, 1),
    ], open_edges=('south',)),
    'ModuleRailgun': compartment(7, 5, [
        *[('DeltaV_SafetyBattery', 1, z, 1) for z in (1, 4, 7)],
        ('DeltaV_SafetyBattery', 4, 1, 1),
    ], vents=(2,), exterior_edges=('north',), rail=5),
    'ModuleMissile': compartment(7, 5, [
        *[('DeltaV_Weapon_MissileLauncher', x, 2) for x in (1, 3, 5)],
        ('DeltaV_Building_MissileMagazine', 2, 2), ('DeltaV_Building_MissileMagazine', 4, 2),
    ], exterior_edges=('north',)),
    'ModuleCasemate': compartment(7, 6, [
        ('DeltaV_Weapon_Casemate', 0, 4), ('DeltaV_Weapon_Casemate', 4, 4),
        ('Shelf', 1, 1, 0, {'allowedDefs': ['DeltaV_SteelHead']}),
        ('ShelfSmall', 5, 1, 0, {'allowedDefs': ['DeltaV_Propellant']}),
        ('ShelfSmall', 3, 3, 0, {'allowedDefs': ['Steel', 'ComponentIndustrial']}),
    ], exterior_edges=('north',), front_layers=2, removable_sides=False),
    'ModuleGuard': compartment(7, 5, [
        ('DeltaV_ShieldGenerator', 1, 1, 0, {'DeltaV_shieldGroundRadius': 8}),
        ('Shelf', 4, 3, 0, {'allowedDefs': ['Silver']}),
    ], entrances=(4,), vents=(5,)),
    'ModuleGenerator': compartment(7, 6, [
        ('ChemfuelPoweredGenerator', x, z) for x in (1, 4) for z in (1, 3)
    ]),
    'ModuleReactor': compartment(7, 10, [
        ('DeltaV_ShipReactor', 1, 3),
        ('DeltaV_SafetyBattery', 1, 1), ('Battery', 4, 1),
        ('DeltaV_SafetyBattery', 1, 7), ('Battery', 4, 7),
    ], entrances=(5,), vents=(4,)),
    'ModuleBridge': compartment(11, 8, [
        ('PilotConsole', 4, 2),
        *[('LargeChemfuelTank', x, z) for x, z in ((1, 1), (7, 1), (1, 4), (4, 4), (7, 4))],
    ], entrances=(5,), vents=(6,)),
    'ModuleFuel': compartment(7, 10, [
        ('LargeChemfuelTank', 1, 3), ('LargeChemfuelTank', 1, 6),
        ('ChemfuelTank', 1, 1), *[('ChemfuelTank', 4, z) for z in (1, 3, 5, 7)],
    ]),
}


def shape(name, compartment_spec=None):
    plan = dict(MODULES[name] if compartment_spec is None else compartment_spec)
    width, base_depth = plan['roomSize']
    depth = base_depth + plan['rail'] + bool(plan['rail'])
    inner = rect(1, 1, width - 2, depth - 1 - plan['frontLayers'])
    divider = plan['divider']
    plan['partitions'] = {c for c in inner if c[0] == divider}
    plan['interior'] = inner - plan['partitions']
    plan['shell'] = rect(0, 0, width - 1, depth - 1) - inner
    plan['barriers'] = rect(3, depth - 1, 5, depth - 1) if plan['rail'] else set()
    plan['cells'] = plan['shell'] | inner
    plan['size'] = (width, depth)
    edges = dict(north=rect(0, depth - 1, width - 1, depth - 1), south=rect(0, 0, width - 1, 0),
                 west=rect(0, 0, 0, depth - 1), east=rect(width - 1, 0, width - 1, depth - 1))
    plan['edges'] = {side: dict(cells=sorted(cells), kind='exterior' if side in plan['exteriorEdges']
                              else 'optional' if side in plan['openEdges'] else 'partition')
                     for side, cells in edges.items()}
    return plan


def adjacent(cells):
    return {(x + dx, z + dz) for x, z in cells for dx, dz in ((0, 1), (1, 0), (0, -1), (-1, 0))}


def flood(start, walkable):
    reached, queue = {start}, deque([start])
    while queue:
        x,z = queue.popleft()
        for cell in ((x+1,z),(x-1,z),(x,z+1),(x,z-1)):
            if cell in walkable and cell not in reached:
                reached.add(cell)
                queue.append(cell)
    return reached


def furnish(name, plan, place, footprint, prop, foundation, roofs, records, occupied):
    for thing, x, z, *options in plan['fixtures']:
        rotation = options[0] if options else 0
        state = options[1] if len(options) > 1 else None
        if thing in battery_capacity:
            state = {'storedPower': float(prop(thing, battery_capacity[thing]))}
        if thing in ('ChemfuelPoweredGenerator', 'DeltaV_ShipReactor'):
            capacity = prop(thing, "comps/li[@Class='CompProperties_Refuelable']/fuelCapacity")
            state = {'fuel': capacity, 'configuredTargetFuelLevel': capacity}
        if thing in ('ChemfuelTank', 'LargeChemfuelTank'):
            state = {'fuel': 0}
        place(thing, x, z, rotation, state=state)
    if plan['rail']:
        depth = plan['roomSize'][1]
        place('DeltaV_Weapon_RailgunLoader', 3, depth - 1)
        for index in range(plan['rail']):
            place('DeltaV_Weapon_RailgunAccelerator', 3, depth + index)
        for cell in sorted(plan['barriers']):
            place('DeltaV_VacuumBarrier', *cell)
    walls = (plan['shell'] | plan['partitions']) - plan['doors'] - plan['vents'] - occupied.keys()
    for x, z in sorted(walls):
        place('GravshipHull', x, z)
    for door in sorted(plan['doors']):
        place('Door', *door)
    for vent in sorted(plan['vents']):
        place('Vent', *vent)
    # Route a single local grid through the pressure compartment and its south
    # connector. Keep every edge of the exposed accelerator chain on the grid.
    transmitters = {c for c, r in occupied.items() if r['transmitsPower']}
    machinery = {c for c in plan['shell'] if occupied[c]['name'].startswith('DeltaV_Weapon_')}
    for x, z in sorted((plan['interior'] | plan['partitions'] | plan['doors'] | plan['vents'] | machinery) - transmitters):
        place('HiddenConduit', x, z, layer='conduit')
    return walls, min(plan['doors'])


def prepare_state(record, state, field, prop):
    sector_layout.prepare_state(record, state, field, prop)
    if not record['stuff'] and (old_stuff := state.find('stuff')) is not None:
        state.remove(old_stuff)
    if record['name'].startswith('DeltaV_Weapon_'):
        for tag in ('combatCoverage', 'manualFireDeviationDeg'):
            for preset in state.findall(tag):
                state.remove(preset)


def validate_and_draw(name, plan, records, foundation, roofs, reachable, here, generated):
    bodies = {c: r for r in records if r['layer'] == 'body' for c in r['cells']}
    empty = (foundation - bodies.keys()) | {c for c, r in bodies.items() if r['name'] == 'StandingLamp'}
    clean = (empty & roofs) | plan['doors']
    if name == 'ModuleFuel':
        # Tanks remain traversable and can be used as a service route.
        clean |= {c for c, r in bodies.items() if r['name'] in ('ChemfuelTank', 'LargeChemfuelTank')
                  and r['passability'] == 'PassThroughOnly'}
    routes = [flood(door, clean) for door in sorted(plan['doors'])]
    assert clean == set().union(*routes), (name, 'isolated clear floor', clean - set().union(*routes))
    assert sum(map(len, routes)) == len(clean), (name, 'independent rooms share a route')
    assert all(c in bodies and bodies[c]['airtight'] for c in plan['shell'] | plan['partitions']), (
        name, 'non-sealing pressure boundary')
    assert all(r['stuff'] == 'Steel' for r in records if r['name'] == 'GravshipHull')
    optional = {tuple(c) for e in plan['edges'].values() if e['kind'] == 'optional' for c in e['cells']}
    assert all(bodies[c]['name'] in ('GravshipHull', 'Door', 'Vent') for c in optional), (
        name, 'fixture on removable edge')
    for x, z in plan['vents']:
        inside = (x, z + 1)
        fixture = bodies.get(inside, {})
        assert (inside in plan['interior'] and fixture.get('passability') != 'Impassable'
                and adjacent({(x, z)}) & plan['doors']), (name, 'vent has no door-side service')
    assert plan['interior'] <= roofs
    assert 5 * len(empty) <= 2 * len(foundation), (name, 'empty foundation over 40%', len(empty))
    interactions = set()
    for r in records:
        if r['layer'] != 'body' or r['name'] in ('GravshipHull', 'Door', 'DeltaV_Weapon_RailgunAccelerator',
                                               'DeltaV_VacuumBarrier'):
            continue
        cells = set(r['cells'])
        service = reachable if name in ('ModuleBridge', 'ModuleRailgun', 'ModuleReactor') else clean
        assert adjacent(cells) & service, (name, 'no service access', r['name'], r['origin'])
        if r['hasInteraction'] and r['name'] != 'DeltaV_CargoStorage':
            ix, _, iz = r['interaction']
            dx, dz = [(ix, iz), (iz, -ix), (-ix, -iz), (-iz, ix)][r['rotation']]
            c = (r['root'][0] + dx, r['root'][1] + dz)
            assert c in clean, (name, 'blocked operation cell', r['name'], c)
            interactions.add(c)
    if plan['rail']:
        assert {c for c, r in bodies.items() if r['name'] == 'DeltaV_VacuumBarrier'} == plan['barriers']
        assert all(bodies[x, z]['name'] == 'GravshipHull' for x in (0, plan['size'][0] - 1)
                   for z in range(plan['size'][1]))
        assert all(adjacent(set(r['cells'])) & reachable for r in records
                   if r['name'] == 'DeltaV_Weapon_RailgunAccelerator')
    magazines = {tuple(r['root']) for r in records if r['name'] == 'DeltaV_Building_MissileMagazine'}
    linked_magazines = set()
    for r in records:
        if r['name'] == 'DeltaV_ShieldGenerator':
            x, z = r['root']
            radius = r['stateOverrides']['DeltaV_shieldGroundRadius']
            assert all((a - x + dx) ** 2 + (b - z + dz) ** 2 <= radius ** 2
                       for a, b in foundation for dx in (-.5, .5) for dz in (-.5, .5))
        if r['name'] in ('DeltaV_Weapon_Casemate', 'DeltaV_Weapon_MissileLauncher'):
            x, z = r['root']
            assert not any(a == x and b > max(p[1] for p in r['cells']) for a, b in foundation), (
                name, 'blocked north-facing muzzle')
            if r['name'] == 'DeltaV_Weapon_MissileLauncher':
                slots = magazines & {(x - 1, z - 1), (x + 1, z - 1)}
                assert slots, (name, 'launcher missing magazine')
                linked_magazines |= slots
    assert linked_magazines == magazines
    if name == 'ModuleCasemate':
        front = {tuple(c) for c in plan['edges']['north']['cells']}
        assert plan['edges']['north']['kind'] == 'exterior'
        assert all(bodies[x, z + dz]['name'] == ('GravshipHull' if x == 3 else 'DeltaV_Weapon_Casemate')
                   for x, z in front for dz in (-1, 0))
        assert interactions == {(1, 3), (5, 3)}

    report = dict(name=name, size=plan['size'], foundation=sorted(foundation), roofs=sorted(roofs),
                  interior=sorted(plan['interior']), partitions=sorted(plan['partitions']),
                  doors=sorted(plan['doors']), vents=sorted(plan['vents']), edges=plan['edges'], clean=sorted(clean),
                  interactions=sorted(interactions), empty=sorted(empty))
    (generated / f'{name}.layout.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    render(report, records, generated / f'{name}.png')
    print(f"{name}: {len(empty)}/{len(foundation)} empty foundation; "
          f"{len(empty & plan['interior'])}/{len(plan['interior'])} clear interior cells")


LABELS = {
    'Door': '门', 'Vent': '风',
    'HiTechResearchBench': '高级研究台', 'MultiAnalyzer': '分析仪',
    'TableMachining': '机床', 'FabricationBench': '精密组装台', 'ToolCabinet': '工具柜',
    'Shelf': '货架', 'ShelfSmall': '架', 'Bed': '床', 'EndTable': '床头柜',
    'DeltaV_CargoStorage': '货柜', 'DeltaV_Weapon_HeavyMainMedium': '重炮',
    'DeltaV_Weapon_RapidMainMedium': '速射', 'DeltaV_Weapon_LaserMainMedium': '激光',
    'DeltaV_Weapon_PDTurret': '点防', 'DeltaV_Weapon_RailgunLoader': '装填机',
    'DeltaV_Weapon_RailgunAccelerator': '加速段', 'DeltaV_ShieldGenerator': '守卫盾',
    'Battery': '电池', 'DeltaV_SafetyBattery': '安全电池',
    'DeltaV_Weapon_MissileLauncher': '发射架', 'DeltaV_Building_MissileMagazine': '弹仓',
    'DeltaV_Weapon_Casemate': '炮廓',
    'DeltaV_VacuumBarrier': '屏', 'ChemfuelPoweredGenerator': '化燃机',
    'DeltaV_ShipReactor': '裂变反应堆', 'LargeChemfuelTank': '大燃料箱', 'ChemfuelTank': '小燃料箱',
    'PilotConsole': '驾驶台',
}
TITLES = dict(zip(MODULES, ('科研舱', '机械加工舱', '精密加工舱', '双独立卧室', '货仓',
                           '双重炮舱', '双速射炮舱', '双激光炮舱', '点防舱', '五段轨道炮舱',
                           '导弹舱', '炮廓舱', '守卫盾舱', '燃料发电舱', '裂变反应堆舱', '驾驶舱', '燃料舱')))
EDGE_COLOR = {'optional': '#58e2c4', 'exterior': '#fa827b'}


def seam(edges_a, edges_b, axis, shared):
    side_a, side_b = (('east', 'west'), ('north', 'south'))[axis]
    a, b = edges_a[side_a], edges_b[side_b]
    if 'exterior' in (a['kind'], b['kind']):
        raise ValueError('An exterior edge cannot be a seam')
    if not shared and (a['kind'], b['kind']) != ('optional', 'optional'):
        raise ValueError('An open seam needs two removable edges')
    if {p[1 - axis] for p in a['cells']} != {p[1 - axis] for p in b['cells']}:
        raise ValueError('Seam lengths or offsets do not match')
    delta = a['cells'][0][axis] - b['cells'][0][axis] - (0 if shared else 1)
    shift = (delta, 0) if axis == 0 else (0, delta)
    return shift, set(map(tuple, a['cells'])), set(map(tuple, b['cells']))


def preview_seam(generated, lower, upper, name, *, shared):
    reports = [json.loads((generated / f'{module}.layout.json').read_text(encoding='utf-8'))
               for module in (lower, upper)]
    shift, edge_a, edge_b = seam(*(r['edges'] for r in reports), axis=1, shared=shared)
    foundation, roofs, clean, interactions = set(), set(), set(), set()
    records = {}
    for module, report, edge, (dx, dz) in zip((lower, upper), reports, (edge_a, edge_b), ((0, 0), shift)):
        cut = set() if shared else edge

        def moved(points):
            return {(x + dx, z + dz) for x, z in points}

        for target, field in ((foundation, 'foundation'), (roofs, 'roofs'),
                              (clean, 'clean'), (interactions, 'interactions')):
            target.update(moved(set(map(tuple, report[field])) - cut))
        source = json.loads((generated / f'{module}.records.json').read_text(encoding='utf-8'))
        for record in source:
            cells = set(map(tuple, record['cells']))
            if cells & cut:
                assert cells <= cut and record['name'] in ('GravshipHull', 'Door', 'Vent', 'HiddenConduit')
                continue
            cells = moved(cells)
            record['cells'] = sorted(cells)
            for field in ('root', 'origin'):
                x, z = record[field]
                record[field] = [x + dx, z + dz]
            key = record['layer'], tuple(sorted(cells))
            if key in records:
                assert shared and cells <= edge_a
                names = {record['name'], records[key]['name']}
                assert names <= {'GravshipHull', 'Door'} or names <= {'GravshipHull', 'Vent'} or names == {'HiddenConduit'}
                if record['name'] not in ('Door', 'Vent'):
                    continue
            records[key] = record

    # Verify the resolved geometry, including the seam's wall/door and grid.
    bodies = {c: r['name'] for r in records.values() if r['layer'] == 'body' for c in r['cells']}
    assert all(c in foundation for c in bodies)
    assert roofs == foundation
    assert clean == flood(min(clean), clean), (name, 'seam interrupts passage')
    power = {c for r in records.values() if r['transmitsPower'] for c in r['cells']}
    assert power == flood(min(power), power), (name, 'seam disconnects power')
    if shared:
        doors = {c for c in clean if bodies.get(c) == 'Door'}
        assert len(edge_a & doors) == 1
        walk = clean - doors
        assert len(flood(min(walk), walk)) < len(walk), (name, 'missing pressure partition')
    else:
        z = min(p[1] for p in edge_a) - 1
        assert any((x, z) in clean and (x, z + 1) in clean for x, _ in edge_a)
        for x in (min(p[0] for p in edge_a), max(p[0] for p in edge_a)):
            assert bodies[x, z] == bodies[x, z + 1] == 'GravshipHull'
    if shared:
        vents = {c for c in edge_a if bodies.get(c) == 'Vent'}
        blocked_air = {c for r in records.values() if r['layer'] == 'body'
                       and (r['airtight'] or r['passability'] == 'Impassable') for c in r['cells']}
        assert len(vents) == 1 and all({(x, z - 1), (x, z + 1)} <= roofs - blocked_air for x, z in vents)
    size = (max(x for x, _ in foundation) + 1, max(z for _, z in foundation) + 1)
    assert size == (7, 10) and len(foundation) == 70
    empty = {c for c in foundation if bodies.get(c) in (None, 'StandingLamp')}
    render(dict(title='机械加工 ＋ 点防｜共用隔墙' if shared else '货仓 ＋ 机械加工｜两侧拆墙',
                name=name, size=size, foundation=foundation, empty=empty, interactions=interactions,
                edges={}, seam=max(p[1] for p in edge_a) - int(not shared), shared=shared),
           records.values(), generated / f'{name}.png')
    print(f'{name}: {size[0]}×{size[1]}, connected path and power')


def preview_joins(generated):
    # Exact 5×5 example, along the other axis: x=4 and x=0 are both cut.
    a = {'east': dict(cells=[(4, z) for z in range(5)], kind='optional')}
    b = {'west': dict(cells=[(0, z) for z in range(5)], kind='optional')}
    (dx, dz), cut_a, cut_b = seam(a, b, axis=0, shared=False)
    joined = (rect(0, 0, 4, 4) - cut_a) | {(x + dx, z + dz) for x, z in rect(0, 0, 4, 4) - cut_b}
    assert joined == rect(0, 0, 7, 4)
    for name in ('ModuleRailgun', 'ModuleMissile', 'ModuleCasemate'):
        for shared in (False, True):
            try:
                seam(shape(name)['edges'], shape('ModulePointDefense')['edges'], axis=1, shared=shared)
            except ValueError as error:
                assert str(error) == 'An exterior edge cannot be a seam'
            else:
                raise AssertionError((name, 'exterior edge accepted as seam'))
    preview_seam(generated, 'ModuleCargo', 'ModuleMachining', 'ModuleJoinOpen', shared=False)
    preview_seam(generated, 'ModuleMachining', 'ModulePointDefense', 'ModuleJoinPartition', shared=True)


def render(report, records, path):
    size = 34
    w, h = report['size']
    image = Image.new('RGB', (max(w * size + 28, 236), h * size + 76), '#121c2b')
    draw = ImageDraw.Draw(image)
    font_dir = Path('C:/Windows/Fonts')
    font = ImageFont.truetype(str(font_dir / 'msyh.ttc'), 11)
    heading = ImageFont.truetype(str(font_dir / 'msyh.ttc'), 15 if 'seam' in report else 19)

    def box(x, z, xx, zz):
        return (14 + x * size, 50 + (h - 1 - zz) * size,
                13 + (xx + 1) * size, 49 + (h - z) * size)

    for x, z in report['foundation']:
        draw.rectangle(box(x, z, x, z), fill='#38536a', outline='#22334a')
    for r in records:
        if r['layer'] != 'body':
            continue
        n = r['name']
        xs, zs = zip(*r['cells'])
        outline = box(min(xs), min(zs), max(xs), max(zs))
        if n == 'StandingLamp':
            x, y, _, _ = outline
            draw.ellipse((x + 5, y + 5, x + 13, y + 13), fill='#d4ecec')
            continue
        color = '#73b6ad'
        if n == 'GravshipHull':
            color = '#8191a1'
        elif n.startswith('DeltaV_Weapon'):
            color = '#d6a283'
        elif n in ('Battery', 'DeltaV_SafetyBattery', 'DeltaV_ShieldGenerator'):
            color = '#a7a6dc'
        elif n == 'Door':
            color = '#edc36c'
        elif n == 'Vent':
            color = '#8bd8f5'
        elif n == 'DeltaV_VacuumBarrier':
            color = '#8adbf7'
        draw.rectangle(outline, fill=color, outline='#22334a')
        if n in LABELS:
            a, b, c, d = outline
            draw.text(((a + c) / 2, (b + d) / 2), LABELS[n], font=font, anchor='mm', fill='#111b2b')
    for x, z in report['interactions']:
        a, b, c, d = box(x, z, x, z)
        draw.ellipse((a + 13, b + 13, c - 13, d - 13), fill='#fff0ac')
    for side, edge in report['edges'].items():
        color = EDGE_COLOR.get(edge['kind'])
        if not color:
            continue
        for x, z in edge['cells']:
            a, b, c, d = box(x, z, x, z)
            line = {'north': (a, b, c, b), 'south': (a, d, c, d),
                    'west': (a, b, a, d), 'east': (c, b, c, d)}[side]
            draw.line(line, fill=color, width=3)
    if 'seam' in report:
        _, a, _, b = box(0, report['seam'], 0, report['seam'])
        y = (a + b) // 2 if report['shared'] else a
        for x in range(15, w * size + 14, 12):
            draw.line((x, y, x + 6, y), fill='#58e2c4', width=2)
    title = report['title'] if 'title' in report else TITLES[report['name']]
    draw.text((14, 8), title, font=heading, fill='white')
    total, empty = len(report['foundation']), len(report['empty'])
    draw.text((14, 32), f'{w}×{h}   空基架 {empty}/{total} · {empty/total:.1%}', font=font, fill='#c9d9ec')
    legend = '绿虚线为拼缝 · 黄点为操作格' if 'seam' in report else '绿边可拆 · 红边朝外 · 黄点为操作格'
    draw.text((14, 56 + h * size), legend, font=font, fill='#c9d9ec')
    image.save(path)
