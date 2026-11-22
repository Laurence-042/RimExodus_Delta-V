"""Original flight decks for the five default sector craft."""
from collections import deque
import math
import re

from sector_settlements import center, rectangle


def cruiser_design():
    axis = 15
    # Bays use their own longitudinal coordinates. Leave one row for each shared bulkhead.
    bays = (
        ('engineering', ((-9, 0, 9, 1), (-9, 2, 8, 5), (-9, 6, 9, 6))),
        ('magazine', ((-10, 0, 10, 6), (-12, 3, 12, 3))),
        ('galley', ((-8, 0, 8, 3),)),
        ('habitation', ((-8, 0, 8, 3),)),
        ('operations', ((-7, 0, 7, 1), (-5, 2, -3, 3), (-1, 2, 1, 3), (3, 2, 5, 3))),
    )
    start = 3
    modules, base, bulkheads = {}, {}, []
    for label, rectangles in bays:
        base[label] = start
        modules[label] = tuple((axis+x0, start+z0, axis+x1, start+z1)
                               for x0, z0, x1, z1 in rectangles)
        start += max(z1 for _, _, _, z1 in rectangles) + 1
        bulkheads.append((start, axis))
        start += 1

    eng, guns, galley, berths, bridge = base.values()
    return dict(
        axis=axis, modules=modules, bulkheads=tuple(bulkheads[:-1]), crew=10, symmetric_shell=False,
        # The starboard reactor gets a third layer. Brake mounts keep an interior service cell.
        armor=((4, eng+2, 4, eng+5), (25, eng+2, 26, eng+5),
               (6, guns, 6, guns), (24, guns, 24, guns),
               (3, guns+1, 3, guns+1), (27, guns+1, 27, guns+1),
               (5, guns+6, 5, guns+6), (25, guns+6, 25, guns+6),
               (5, galley, 5, galley+3), (25, galley, 25, galley+3),
               (21, galley+3, 23, galley+3),
               (5, berths, 5, berths), (25, berths, 25, berths),
               (7, berths+3, 7, berths+3), (23, berths+3, 23, berths+3),
               (13, bridge+5, 17, bridge+5)),
        deck=((1, guns, 3, guns), (1, guns-1, 2, guns-1)),
        port=(28, guns+3, 1), exterior_doors=((4, guns, 3),),
        drives=((5, eng-2, 0), (7, eng-2, 0), (10, eng-2, 0), (12, eng-2, 0),
                (3, guns+5, 2), (5, berths+2, 2), (8, bridge+2, 2),
                (4, eng+1, 1), (4, eng+6, 1), (3, guns+2, 1), (5, berths+1, 1)),
        thrust=(100, 100, 90),
        weapon_pairs=(('HeavyMainMedium', 6, guns+4, 'magazine'),
                      ('RapidMainMedium', 9, guns, 'magazine', 2),
                      ('LaserMainMedium', 10, bridge+1, 'operations'),
                      ('PDTurret', 1, guns, 'deck'), ('PDTurret', 8, guns, 'magazine')),
        shields=(('magazine', 14, guns+4), ('engineering', 7, eng+2)),
        bridge=(14, bridge+2), backup_console=('engineering', (10, eng+4)),
        emergency_power=(12, guns), emergency_battery=(18, guns+5),
        machinery=(17, eng+2),
        service_aisle=((15, eng, 15, eng+6), (3, guns+3, 27, guns+3), (15, galley, 15, galley+3),
                       (10, berths, 10, berths+2), (20, berths, 20, berths+2),
                       (10, berths+2, 20, berths+2), (15, berths, 15, berths+3),
                       (15, bridge, 15, bridge+1)),
        bunks=((11, berths, 1), (13, berths, 0), (11, berths+1, 1), (11, berths+3, 1), (9, berths+1, 0)),
        power='engineering', quarters='habitation')


# Interior floor rectangles are (west, south, east, north), inclusive.
# Build the pressure shell from the rooms, then add exterior service paths and mounts.
CRAFT = {
    'SectorFighter': dict(
        axis=7,
        modules={
            'engineering': ((3, 2, 3, 4), (11, 2, 11, 4), (4, 3, 4, 4), (10, 3, 10, 4),
                            (7, 3, 7, 3), (4, 4, 10, 6), (3, 6, 11, 6)),
            'battery': ((3, 7, 11, 8), (1, 9, 13, 9), (2, 10, 12, 10), (3, 11, 11, 11)),
            'cockpit': ((5, 12, 9, 13), (6, 14, 8, 15)),
        },
        open_plan=True,
        deck=(), port=(14, 9, 1), exterior_doors=(),
        bulkheads=(), crew=1,
        drives=((2, 0, 0), (5, 2, 0), (1, 11, 2), (2, 5, 1), (0, 10, 1)),
        thrust=(100, 100, 100),
        weapon_pairs=(('RapidMainSmall', 3, 8, 'battery'), ('PDTurret', 3, 11, 'battery')),
        bridge=(6, 14), machinery=(6, 5), power='engineering', quarters='battery'),
    'SectorGunboat': dict(
        axis=10,
        modules={
            'engineering': ((7, 3, 13, 7), (8, 8, 12, 8)),
            'battery': ((4, 10, 16, 10), (5, 11, 15, 11), (4, 12, 16, 12), (5, 13, 15, 15)),
            'bridge': ((8, 17, 12, 17), (5, 18, 15, 18), (7, 19, 13, 21)),
        },
        deck=((7, 0, 10, 2), (2, 19, 5, 23), (1, 10, 3, 10), (2, 18, 4, 18)),
        port=(17, 12, 1), exterior_doors=((6, 20, 3), (10, 2, 2), (3, 10, 3), (4, 18, 3)),
        bulkheads=((9, 10), (16, 10)), crew=2,
        cooler=(14, 3, 1),
        drives=((7, 0, 0), (3, 22, 2), (1, 10, 1), (2, 18, 1)),
        thrust=(90, 73, 57),
        weapon_pairs=(('RapidMainSmall', 5, 13, 'battery'), ('PDTurret', 4, 21, 'deck')),
        weapons=(('RapidMainMedium', 9, 11, 'battery'),),
        bridge=(9, 20), machinery=(9, 3), generator_pairs=((7, 3), (7, 5)),
        service_aisle=((9, 3, 11, 7), (10, 8, 10, 8), (6, 15, 14, 15)),
        power='engineering', quarters='battery'),
    'SectorFrigate': dict(
        axis=12,
        modules={
            'engineering': ((10, 3, 14, 3), (9, 4, 15, 6), (7, 7, 17, 7),
                            (8, 8, 16, 10), (5, 11, 19, 11)),
            'habitation': ((8, 13, 16, 18), (7, 16, 17, 16)),
            'operations': ((6, 20, 18, 20), (8, 21, 16, 23), (9, 24, 15, 27), (10, 28, 14, 30)),
        },
        deck=((2, 0, 5, 1), (2, 2, 6, 2), (3, 3, 6, 6), (2, 7, 6, 8),
              (2, 11, 4, 11), (3, 20, 5, 20), (9, 32, 12, 32)),
        port=(18, 16, 1), exterior_doors=((6, 7, 3), (4, 11, 3), (5, 20, 3)),
        bulkheads=((12, 12), (19, 12)), crew=4,
        cooler=(8, 4, 3),
        drives=((2, 0, 0), (4, 0, 0), (9, 31, 2), (2, 11, 1), (3, 20, 1)),
        thrust=(85, 84, 70),
        weapon_pairs=(('RapidMainMedium', 8, 21, 'operations'), ('PDTurret', 4, 5, 'deck')),
        weapons=(('LaserMainMedium', 11, 25, 'operations'),),
        shields=(('engineering', 11, 8),), bridge=(11, 29), machinery=(10, 3),
        service_aisle=((12, 3, 12, 7), (7, 7, 17, 7)),
        power='engineering', quarters='habitation'),
    'SectorDestroyer': dict(
        axis=14,
        modules={
            'engineering': ((9, 4, 19, 5), (7, 6, 21, 6), (5, 7, 23, 8), (7, 9, 21, 9)),
            'battery': ((6, 11, 22, 14),),
            'habitation': ((9, 16, 19, 19), (6, 18, 22, 18)),
            'weapons': ((10, 21, 18, 21), (8, 22, 20, 22), (11, 23, 17, 24),
                        (10, 25, 18, 26), (12, 27, 16, 28)),
        },
        deck=((7, 0, 14, 2), (1, 8, 4, 8),
              (5, 21, 7, 21), (5, 22, 6, 24), (12, 30, 14, 36)),
        port=(23, 18, 1), exterior_doors=((12, 29, 0), (9, 3, 2),
                                        (4, 8, 3), (7, 22, 3)),
        bulkheads=((10, 14), (15, 14), (20, 14)), crew=6,
        cooler=(4, 7, 3),
        drives=((7, 0, 0), (11, 0, 0), (10, 28, 2), (5, 23, 2), (1, 8, 1), (5, 21, 1)),
        thrust=(100, 80, 78),
        weapon_pairs=(('RapidMainMedium', 6, 12, 'battery'),
                      ('LaserMainMedium', 10, 12, 'battery'), ('PDTurret', 12, 34, 'deck')),
        shields=(('engineering', 10, 6),), rail=(13, 29, 7), bridge=(10, 25),
        machinery=(12, 4), service_aisle=((14, 4, 14, 9), (9, 11, 9, 13), (19, 11, 19, 13),
                                          (14, 16, 14, 19), (6, 18, 22, 18)),
        power='engineering', quarters='habitation'),
    'SectorCruiser': cruiser_design(),
}

FACING = ((0, 1), (1, 0), (0, -1), (-1, 0))


def adjacent(cell):
    x, z = cell
    return [(x+dx, z+dz) for dx, dz in FACING]


def drive_mounts(spec):
    for x, z, rotation, *kind in spec['drives']:
        yield kind[0] if kind else ('SmallThruster' if rotation % 2 else 'LargeThruster'), x, z, rotation


def shape(name, footprint):
    spec = CRAFT[name]
    armor = set().union(*(rectangle(b) for b in spec.get('armor', ())))
    rooms = {label: set().union(*(rectangle(b) for b in boxes)) - armor for label, boxes in spec['modules'].items()}
    floor = set().union(*rooms.values())
    axis = spec['axis']
    if spec.get('symmetric_shell', True):
        assert floor == {(2*axis-x, z) for x, z in floor}, name
    assert len(floor) == sum(map(len, rooms.values())), (name, 'overlapping modules')
    if not spec.get('open_plan'):
        for label, room in rooms.items():
            assert not any(n in floor - room for c in room for n in adjacent(c)), (name, label, 'missing bulkhead')
    hull = floor | {n for c in floor for n in adjacent(c)} | armor
    west = {c for b in spec['deck'] for c in rectangle(b) if c[0] <= axis}
    for thing, x, z, rotation in drive_mounts(spec):
        west.update(footprint(thing, x, z, rotation)[2])
    exposed = (west | {(2*axis-x, z) for x, z in west}) - hull
    cells = hull | exposed
    return dict(size=(max(x for x, _ in cells)+1, max(z for _, z in cells)+1),
                cells=cells, hull=hull, module_floors=rooms, noRoofCells=exposed, mirrorAxis=axis,
                symmetricShell=spec.get('symmetric_shell', True), armorCells=armor)


def furnish(name, plan, foundation, roofs, records, occupied, reserved, put, auto, footprint, prop,
            life_support, finish_structure):
    spec = CRAFT[name]
    hull, deck = plan['hull'], plan['noRoofCells']
    axis = spec['axis']

    def put_mirrored(thing, x, z, rot=0, **kwargs):
        _, _, cells = footprint(thing, x, z, rot)
        reflected = {(2*axis-a, b) for a, b in cells}
        if reflected == cells:
            assert rot % 2 == 0, (name, thing, 'centerline fixture faces sideways')
            return (put(thing, x, z, rot, **kwargs),)
        rx = min(a for a, _ in reflected)
        _, _, right = footprint(thing, rx, z, -rot % 4)
        assert right == reflected and not cells & right, (name, thing, 'invalid mirrored footprint')
        pair_id = len(records)
        pair = (put(thing, x, z, rot, **kwargs), put(thing, rx, z, -rot % 4, **kwargs))
        for record in pair:
            record['mirrorPair'] = pair_id
        return pair

    engines = []
    for thing, x, z, rotation in drive_mounts(spec):
        thrust = spec['thrust'][2 if rotation % 2 else rotation // 2]
        engines.extend(put_mirrored(thing, x, z, rotation, state={'DeltaV_powerPercent': thrust}))
    assert all(r['airtight'] for r in engines)
    engine_cells = {tuple(c) for r in engines for c in r['cells']}
    roofs.difference_update(deck | engine_cells)
    plan['noRoofCells'] = deck | engine_cells
    rooms = plan['module_floors']
    floor = set().union(*rooms.values())
    assert not engine_cells & floor, (name, 'drive intrudes into pressurized floor')
    walls = hull - floor - engine_cells

    mouths = []
    for door in put_mirrored('DeltaV_DockingPort', *spec['port']):
        x, z = door['origin']
        dx, dz = FACING[door['rotation']]
        assert (x+dx, z+dz) not in foundation, (name, 'blocked docking approach')
        walls.remove((x, z))
        mouths.append((x-dx, z-dz))
    for origin in spec['exterior_doors']:
        for door in put_mirrored('Door', *origin):
            x, z = door['origin']
            dx, dz = FACING[door['rotation']]
            outside = (x+dx, z+dz)
            assert outside in deck, (name, 'missing exterior service deck', outside)
            reserved.add(outside)
            walls.remove((x, z))
            mouths.append((x-dx, z-dz))
    for z, x in spec['bulkheads']:
        walls.remove((x, z))
        put('Door', x, z)
        mouths.extend(((x, z-1), (x, z+1)))
    if 'rail' in spec:
        x, z, count = spec['rail']
        loader = put('DeltaV_Weapon_RailgunLoader', x, z, state={'groupNumber': '4', 'salvoSet': 'Main'})
        walls.difference_update(map(tuple, loader['cells']))
        for index in range(1, count+1):
            put('DeltaV_Weapon_RailgunAccelerator', x, z+index)
        assert (loader['root'][0], z+count+1) not in foundation
    if 'cooler' in spec:
        x, z, rotation = spec['cooler']
        dx, dz = FACING[rotation]
        assert (x+dx, z+dz) not in foundation
        walls.remove((x, z))
        reserved.add((x-dx, z-dz))
        put('Cooler', x, z, rotation, state={'targetTemperature': 21})
    for x, z in sorted(walls):
        put('GravshipHull', x, z)
    assert plan['armorCells'] <= walls, (name, 'armor interrupted by a fixture')

    assert set(mouths) <= floor, (name, 'sealed doorway', set(mouths) - floor)
    reserved.update(mouths)
    def routes(entries, area):
        parents = {cell: None for cell in entries}
        queue = deque(parents)
        while queue:
            parent = queue.popleft()
            for cell in adjacent(parent):
                if cell in area and cell not in parents:
                    parents[cell] = parent
                    queue.append(cell)
        return parents

    def keep_route(target, parents):
        while target is not None:
            reserved.add(target)
            target = parents[target]

    assert len(routes(mouths[:1], foundation)) == len(foundation), (name, 'disconnected module or mount')

    def interaction_cell(record):
        dx, _, dz = record['interaction']
        dx, dz = ((dx, dz), (dz, -dx), (-dx, -dz), (-dz, dx))[record['rotation']]
        x, z = record['root']
        return (x+dx, z+dz)

    auto('PilotConsole', rooms[next(reversed(rooms))], rotations=(0,), near=spec['bridge'], center_axis=axis)
    power_room = rooms[spec['power']]
    service_aisle = set().union(*(rectangle(b) for b in spec.get('service_aisle', ())))
    assert service_aisle <= floor, (name, 'service aisle crosses the hull')
    reserved.update(service_aisle)
    if 'backup_console' in spec:
        label, origin = spec['backup_console']
        auto('PilotConsole', rooms[label], rotations=(0,), near=origin)
        reactor = put('DeltaV_ShipReactor', *spec['machinery'],
                      state={'fuel': 120, 'configuredTargetFuelLevel': 120})
        assert set(map(tuple, reactor['cells'])) <= power_room - reserved
    if 'emergency_power' in spec:
        for generator in put_mirrored('ChemfuelPoweredGenerator', *spec['emergency_power'],
                                      state={'fuel': 30, 'configuredTargetFuelLevel': 30}):
            assert set(map(tuple, generator['cells'])) <= rooms['magazine'] - reserved

    def chemfuel_generator(**placement):
        return auto('ChemfuelPoweredGenerator', power_room,
                    state={'fuel': 30, 'configuredTargetFuelLevel': 30}, **placement)

    if name == 'SectorFighter':
        chemfuel_generator(near=spec['machinery'])
        auto('Battery', power_room, near=(4, 11), mirror_axis=axis)
    for origin in spec.get('generator_pairs', ()):
        chemfuel_generator(near=origin, mirror_axis=axis)
    for key, x, z in spec.get('shields', ()):
        shield = auto('DeltaV_ShieldGenerator', rooms[key], near=(x, z),
                      state={'DeltaV_shieldProjection': '28', 'DeltaV_shieldLength': '28'})
        if name == 'SectorCruiser':
            defn = 'DeltaV_ShieldGenerator'
            fuel = float(prop(defn, "comps/li[@Class='CompProperties_Refuelable']/fuelCapacity"))
            sx, sz = (coord+0.5 for coord in shield['root'])
            # GuardCenter is the root cell center, even for a multi-cell generator.
            radius = math.ceil(max(math.hypot(x+dx-sx, z+dz-sz) for x, z in foundation
                                   for dx, dz in ((0, 0), (0, 1), (1, 0), (1, 1)))) + 1
            shield_props = "comps/li[@Class='DeltaV.CompProperties_SpaceShield']"
            assert radius <= int(prop(defn, shield_props + '/maxGroundRadius', '40'))
            shield['stateOverrides'].update(
                DeltaV_shieldMode='Guard', DeltaV_shieldGroundRadius=radius,
                DeltaV_regularCapacity=float(prop(defn, shield_props + '/regularCapacity', '750')),
                currentHitPoints=-1, fuel=fuel, configuredTargetFuelLevel=fuel)
    for weapon, x, z, label in spec.get('weapons', ()):
        auto('DeltaV_Weapon_' + weapon, rooms[label], near=(x, z), center_axis=axis)
    for weapon, x, z, label, *rotation in spec['weapon_pairs']:
        def_name = 'DeltaV_Weapon_' + weapon
        if label == 'deck':
            guns = put_mirrored(def_name, x, z)
        else:
            guns = auto(def_name, rooms[label], near=(x, z), mirror_axis=axis, rotations=tuple(rotation or [0]))
        if name == 'SectorFighter' and weapon == 'RapidMainSmall':
            slot = prop(def_name, "comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/attachmentSlots/li/cell")
            dx, _, dz = map(int, re.findall(r'-?\d+', slot))
            for gun in guns:
                rx, rz = gun['root']
                assert (rx+dx, rz+dz) in map(tuple, gun['cells'])
                put('DeltaV_Building_AutoAimPluginI', rx+dx, rz+dz, layer='attachment')
    if 'rail' in spec:
        loader_cost = float(prop('DeltaV_Weapon_RailgunLoader',
            "comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/shotEnergy"))
        segment_cost = float(prop('DeltaV_Weapon_RailgunAccelerator',
            "comps/li[@Class='DeltaV.CompProperties_SpaceRailgun']/shotEnergy"))
        capacity = float(prop('DeltaV_SafetyBattery',
            "comps/li[@Class='DeltaV.CompProperties_SwitchableBattery']/storedEnergyMax"))
        count = math.ceil((loader_cost + spec['rail'][2]*segment_cost) / capacity)
        for _ in range(count):
            auto('DeltaV_SafetyBattery', rooms['weapons'], state={'storedPower': capacity},
                 near=center(rooms['weapons']))

    quarters = rooms[spec['quarters']]
    if 'bunks' in spec:
        assert len(spec['bunks'])*2 == spec['crew']
        for origin in spec['bunks']:
            for bed in put_mirrored('Bed', *origin):
                assert set(map(tuple, bed['cells'])) <= quarters - reserved
    elif name == 'SectorDestroyer':
        for _ in range(spec['crew']//2):
            auto('Bed', quarters, near=(axis-2, center(quarters)[1]), mirror_axis=axis)

    # Reserve a connected route after installing the largest equipment.
    # Include rail access and deck operators in the same traversal.
    def walkable():
        return foundation - {c for c, r in occupied.items()
                             if r['passability'] != 'Standable'
                             and r['name'] not in ('Door', 'DeltaV_DockingPort')}

    parents = routes(mouths[:1], walkable())
    targets = set(mouths) | {interaction_cell(r) for r in records if r['hasInteraction']}
    if name in ('SectorFighter', 'SectorCruiser'):
        for engine in engines:
            approaches = {n for c in map(tuple, engine['cells']) for n in adjacent(c)
                          if n in parents and n in roofs}
            assert approaches, (name, engine['origin'], 'engine has no cabin approach')
            targets.add(min(approaches))
    assert targets <= parents.keys(), (name, 'blocked route', targets - parents.keys(),
                                     [(r['name'], r['origin'], r['rotation'], interaction_cell(r))
                                      for r in records if r['hasInteraction']])
    for target in targets:
        keep_route(target, parents)

    def supply(room, definitions, near=None):
        cabinet = auto('Shelf', room, state={'allowedDefs': [thing for thing, _ in definitions]}, near=near)
        cells = cabinet['cells']
        assert len(definitions) <= len(cells) * int(prop('Shelf', 'building/maxItemsInCell'))
        for index, (thing, count) in enumerate(definitions):
            cell = cells[index % len(cells)]
            put(thing, *cell, layer='items', state={'stackCount': count})

    if name == 'SectorGunboat':
        supply(power_room, [('Chemfuel', 75), ('ComponentIndustrial', 10)], near=(7, 3))
    if name == 'SectorCruiser':
        auto('DeltaV_EvaSuit', deck, near=(1, min(z for x, z in rooms['magazine'])-1), mirror_axis=axis)
        galley = rooms['galley']
        south = min(z for x, z in galley)
        lamp = auto('SunLamp', galley, near=(20, south+3))
        for x, z in ((16, south), (20, south), (16, south+2), (20, south+2)):
            _, _, cells = footprint('HydroponicsBasin', x, z, 1)
            assert cells <= galley - reserved and all(math.dist(c, lamp['root']) <= 5.5 for c in cells)
            basin = put('HydroponicsBasin', x, z, 1, state={'plantDefToGrow': 'Plant_Rice'})
            for cell in basin['cells']:
                put('Plant_Rice', *cell, layer='plants', state={'growth': 1})
        auto('ChessTable', galley, near=(8, south+1))
        auto('ElectricStove', galley, near=(10, south+1))
        supply(galley, [('RawRice', 75), ('Steel', 75)], near=(13, south+3))

    if name == 'SectorFighter':
        auto('HospitalBed', quarters, near=(7, 7))
    elif name not in ('SectorDestroyer', 'SectorCruiser'):
        for _ in range(spec['crew']//2):
            auto('Bed', quarters, near=(axis-2, center(quarters)[1]), mirror_axis=axis)
    if name in ('SectorFrigate', 'SectorCruiser'):
        mess = rooms.get('galley', quarters)
        table = auto('Table2x2c', mess, near=(8, south+2) if name == 'SectorCruiser' else center(mess))
        x, z = table['origin']
        for px, pz, face in ((x-1,z,1), (x+2,z+1,3), (x,z-1,0), (x+1,z+2,2)):
            cell = (px, pz)
            if cell in mess and cell not in reserved and cell not in occupied:
                put('DiningChair', px, pz, face)
    supply(quarters, [('MealSurvivalPack', 10), ('MedicineIndustrial', 10)])
    magazine = rooms.get('magazine', rooms.get('battery', quarters))
    ammunition = [('DeltaV_Propellant', 40), ('DeltaV_SteelHead', 40)]
    if name == 'SectorDestroyer':
        ammunition.append(('DeltaV_UraniumHead', 60))
    supply(magazine, ammunition, near=center(magazine))
    if 'emergency_power' in spec:
        supply(magazine, [('Chemfuel', 75), ('ComponentIndustrial', 10)], near=spec['emergency_power'])

    compartments = {'cockpit': floor} if spec.get('open_plan') else rooms
    for label, room in compartments.items():
        life_support(room, walls, center(room), label)
        auto('StandingLamp', room, near=center(room))
    demand = sum(max(0, r['powerWatts']) for r in records)
    if name == 'SectorCruiser':
        supply(power_room, [('Uranium', 75), ('ComponentIndustrial', 15)], near=spec['machinery'])
    else:
        watts = -sum(r['powerWatts'] for r in records if r['name'] == 'ChemfuelPoweredGenerator')
        while watts < demand + (0 if name == 'SectorFighter' else 1200):
            watts -= chemfuel_generator(near=spec['machinery'])['powerWatts']
        if name != 'SectorGunboat':
            supply(power_room, [('Chemfuel', 75), ('ComponentIndustrial', 10)], near=spec['machinery'])
    if 'rail' not in spec and name != 'SectorFighter':
        battery_room = rooms['battery'] if name == 'SectorGunboat' else power_room
        auto('Battery', battery_room, near=center(battery_room))
    if 'emergency_battery' in spec:
        reserve = auto('DeltaV_SafetyBattery', magazine, rotations=(0,), near=spec['emergency_battery'],
                       state={'storedPower': float(prop('DeltaV_SafetyBattery',
                           "comps/li[@Class='DeltaV.CompProperties_SwitchableBattery']/storedEnergyMax"))})
        assert all(c[0] > axis for c in reserve['cells'])
    finish_structure(roofs)
    walk = routes(mouths[:1], walkable())
    assert (set(mouths) | service_aisle) <= walk.keys(), (name, 'blocked doorway or engineering aisle')
    crew_cells = quarters & walk.keys() - occupied.keys()
    assert len(crew_cells) >= spec['crew'], (name, 'no clear crew spawn cells')
    anchor = center(quarters)
    for cell in sorted(crew_cells, key=lambda c: (math.dist(c, anchor), c))[:spec['crew']]:
        put('DeltaV_PrefabPawnMarker', *cell)
    for r in records:
        if r['hasInteraction']:
            assert interaction_cell(r) in walk, (name, r['name'], 'access crosses equipment', interaction_cell(r))
    if name == 'SectorFighter':
        assert foundation - roofs == engine_cells, (name, 'bare exterior deck')
    for r in records:
        if r['layer'] != 'body' or r['name'] in ('GravshipHull', 'DeltaV_DockingPort', 'DeltaV_PrefabPawnMarker'):
            continue
        cells = set(map(tuple, r['cells']))
        if not cells <= floor and not (name in ('SectorFighter', 'SectorCruiser') and r in engines):
            continue
        assert any(n in walk and n in roofs and n not in cells for c in cells for n in adjacent(c)), (
            name, r['name'], r['origin'], 'no covered service approach')
    palette = {'cockpit': 'Structure_BlueSubtle', 'bridge': 'Structure_BlueSubtle',
               'operations': 'Structure_BlueSubtle', 'engineering': 'Structure_BrownSubtle',
               'battery': 'Structure_RedSubtle', 'weapons': 'Structure_RedSubtle',
               'magazine': 'Structure_Mustard', 'galley': 'Structure_GreenPine', 'habitation': 'Structure_Teal'}
    plan['rooms'] = [dict(color=palette[key], cells=cells) for key, cells in rooms.items()]
    plan['modules'] = rooms
    plan['aisles'] = service_aisle
    return walls, mouths[0]
