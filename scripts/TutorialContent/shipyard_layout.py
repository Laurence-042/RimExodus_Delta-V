"""Approved ring station and gift freighter, authored in local grid coordinates.

build.py resolves every placement through the current Defs and serializes it.
The drawing and checks consume those same records; this module never spawns Things.
"""
import math
import json
from collections import defaultdict, Counter, deque
from pathlib import Path
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageFont
from station_layout import FACING, center, furnishing_grid, rectangle, solar_budget, prepare_trade_state

DIRS=((1,0),(-1,0),(0,1),(0,-1))
NAMES=['生活', '农业', '工坊', '物流', '能源', '防护', '贸易', '住宿']
COLORS=['#28545c','#325f49','#665039','#65513a','#574c68','#3b536a','#315265','#405276']
ARM_CUT = (13, 48)
ARM_CUT_CELLS = {(ARM_CUT[0]+dx, ARM_CUT[1]) for dx in (-1, 0, 1)}
ARM_ROOMS = {'arm', 'cargo', 'stores'}
YARD_HEATER_TARGET = 25
PLANTING_TEMPLATE = Path(__file__).with_name('Module_13x13_Planting.prefab')


def planting_module(room, turn):
    x, z = min(room)

    def cell(node):
        a, _, b = map(int, node.findtext('pos').strip('()').split(','))
        return (x+11-a, z+11-b) if turn else (x+a-1, z+b-1)

    template = ET.parse(PLANTING_TEMPLATE)
    assert template.findtext('prefab/size') == '(13, 13)'
    return ([(node, cell(node)) for node in template.findall('thingStates/li')],
            {cell(node): node.findtext('def') for node in template.findall('foundations/li')},
            {cell(node): node.findtext('def') for node in template.findall('roofs/li')})


def adj(c): return [(c[0]+x,c[1]+z) for x,z in DIRS]
def components(cells):
    remaining=set(cells); result=[]
    while remaining:
        seed=min(remaining); found={seed}; q=deque([seed]);remaining.remove(seed)
        while q:
            for n in adj(q.popleft()):
                if n in remaining: remaining.remove(n);found.add(n);q.append(n)
        result.append(found)
    return result

def shape(name):
    if name=='PrototypeShip':
        cells={(x,z+4) for x in range(17) for z in range(25)
               if not(z>=23 and (x<3 or x>13)) and not(21<=z<=22 and (x<1 or x>15))
               and not(z<=2 and (x<2 or x>14))}
        cells|={(x,z) for x in range(2,15) for z in range(4)}
        return dict(size=(17,29),cells=cells,zones={c:2 for c in cells},foundationDefs={})
    return yard_shape()


def yard_shape():
    # Coordinates are relative to the central landing and crew muster point.
    rooms = {
        'trade': (-54, 3, -44, 11), 'waiting': (-54, 13, -44, 17),
        'kitchen': (-19, 3, -15, 10), 'dining': (-13, 3, -3, 10),
        'control': (5, 3, 12, 10), 'medical': (14, 3, 20, 8),
        'lounge': (-10, 12, -3, 17), 'guest': (3, 12, 9, 17),
        'cargo': (-52, -24, -34, -17), 'power': (3, -10, 19, -3),
        'stores': (-47, -32, -39, -26), 'research': (3, -19, 12, -12),
        'workshop': (46, 3, 54, 11), 'repair': (46, -9, 54, -3),
        'moon': (-12, -19, -3, -3),
    }
    corridors = [(-55, -1, 55, 1), (-1, -55, 1, 55), (-42, 1, -41, 17),
                 (-31, 26, 31, 28), (-31, -28, 31, -26)]
    for end, sign in (('north', 1), ('south', -1)):
        def orient(x0, z0, x1, z1):
            return (x0, z0, x1, z1) if sign == 1 else (x0, -z1, x1, -z0)

        for side, x in (('west', -15), ('east', 3)):
            rooms[f'farm-{end}-{side}'] = orient(x+1, 30, x+11, 40)
        for x in (-31, -26, -21, 17, 22, 27):
            rooms[f'cabin-{end}-{x}'] = orient(x, 30, x+3, 34)
        for side, x in (('west', -30), ('east', 22)):
            rooms[f'defense-{end}-{side}'] = orient(x+1, 19, x+7, 24)

    def grid(bounds):
        return rectangle(tuple(v+56 for v in bounds))

    ring = {(x, z) for x in range(29, 84) for z in range(29, 84)
            if 23.5**2 <= (x-56)**2 + (z-56)**2 <= 27**2}
    corridor = ring | set().union(*(grid(bounds) for bounds in corridors))
    modules = {key: grid(bounds) for key, bounds in rooms.items()}
    modules['moon'].update(rectangle((37, 44, 43, 53)) | rectangle((39, 42, 43, 43)))
    # The wider landings leave room for life support while the middle stays
    # one walkable cell wide between its two hull walls.
    modules['arm'] = (rectangle((12, 41, 14, 44)) | rectangle((13, 45, 13, 50))
                      | rectangle((12, 51, 14, 53)))
    interior = set(corridor)
    for key, room in modules.items():
        assert not interior & room and all(not interior.intersection(adj(c)) for c in room), (
            key, 'missing bulkhead', sorted(c for c in room if c in interior or interior.intersection(adj(c))))
        interior.update(room)
    assert len(components(corridor)) == 1, 'disconnected circulation'
    # Square corners close the room bulkheads. Ring walls follow the local grid edge.
    room_cells = set().union(*modules.values())
    cells = interior | {n for c in interior for n in adj(c)}
    cells.update((x+dx, z+dz) for x, z in room_cells for dx in (-1, 1) for dz in (-1, 1))
    assert cells <= rectangle((0, 0, 112, 112))
    palette = {key: zone for zone, keys in enumerate((
        ('dining', 'kitchen', 'control', 'medical', 'lounge', 'guest'),
        ('farm',), ('workshop', 'repair', 'research'), ('cargo', 'stores', 'moon', 'arm'),
        ('power',), ('defense',), ('trade', 'waiting'), ('cabin',)))
        for key in keys}
    zones = {c: palette[key.split('-')[0]] for key, room in modules.items() for c in room}
    farms = {key: planting_module(room, 2 if '-south-' in key else 0)
             for key, room in modules.items() if key.startswith('farm-')}
    return dict(size=(113, 113), cells=cells, modules=modules, corridor=corridor, ring=ring,
                zones=zones, foundationDefs={}, farms=farms)


def yard_partitions(plan, foundation, put, reserved):
    modules, corridor = plan['modules'], plan['corridor']
    walls = foundation - corridor - set().union(*modules.values())
    ports = {(0, 56): 3, (112, 56): 1, (56, 0): 2}
    for (x, z), rotation in ports.items():
        dx, dz = FACING[rotation]
        assert (x-dx, z-dz) in corridor and (x+dx, z+dz) not in foundation
        walls.remove((x, z))
        put('DeltaV_DockingPort', x, z, rotation)
        reserved.add((x-dx, z-dz))

    doors, vents = set(), []
    def open_door(x, z, rotation):
        dx, dz = FACING[rotation]
        walls.remove((x, z))
        put('Door', x, z, rotation)
        doors.add((x, z))
        reserved.update(((x-dx, z-dz), (x+dx, z+dz)))

    def connect(room, other, label):
        midpoint = center(room)
        choices = [(math.dist((x, z), midpoint), x, z, rotation)
                   for x, z in walls for rotation, (dx, dz) in enumerate(FACING)
                   if (x-dx, z-dz) in room and (x+dx, z+dz) in other]
        assert choices, (label, 'missing shared wall')
        _, x, z, rotation = min(choices)
        open_door(x, z, rotation)
        return x, z, rotation

    adjoining = {'cargo': 'arm', 'stores': 'cargo'}
    for key, room in modules.items():
        if key in plan['farms']:
            things, _, _ = plan['farms'][key]
            turn = 2 if '-south-' in key else 0
            for node, cell in things:
                name = node.findtext('def')
                if name == 'Door':
                    open_door(*cell, turn)
                elif name == 'Vent':
                    walls.remove(cell)
                    put('Vent', *cell, turn, state={'switchOn': 'True', 'wantSwitchOn': 'True'})
                    vents.append(dict(cell=cell, rooms=('corridor', key)))
            continue
        other = modules[adjoining[key]] if key in adjoining else corridor
        x, z, _ = connect(room, other, key)
        if key in ARM_ROOMS:
            continue
        candidates = [(abs(a-x)+abs(b-z), a, b, facing)
                      for a, b in walls
                      # Vacuum also exchanges along the sides of a vent in the vanilla grid.
                      if all(n in foundation for n in adj((a, b)))
                      for facing, (dx, dz) in enumerate(FACING)
                      if (a-dx, b-dz) in room and (a+dx, b+dz) in corridor]
        count = min(len(candidates), 1 if len(room) <= 24 else 2)
        assert count, (key, 'missing pressure vent')
        for _, a, b, facing in sorted(candidates)[:count]:
            walls.remove((a, b))
            put('Vent', a, b, facing, state={'switchOn': 'True', 'wantSwitchOn': 'True'})
            vents.append(dict(cell=(a, b), rooms=(key, 'corridor')))
            dx, dz = FACING[facing]
            reserved.update(((a-dx, b-dz), (a+dx, b+dz)))
    connect(modules['kitchen'], modules['dining'], 'kitchen to dining')
    # Array exits are sealed by doors and lead to adjacent unroofed service decks.
    for door in ((54, 7, 3), (58, 7, 1), (54, 105, 3), (58, 105, 1)):
        open_door(*door)
    for x, z in sorted(walls):
        put('GravshipHull', x, z)

    # Equipment can use the ring edges. Keep the center of the ring and main spine open.
    reserved.update(c for c in plan['ring'] if 24.5**2 <= (c[0]-56)**2 + (c[1]-56)**2 <= 26**2)
    reserved.update((x, 56) for x in range(1, 112))
    reserved.update((56, z) for z in range(1, 112))
    reserved.update((13, z) for z in range(41, 54))
    plan['vents'] = vents
    return walls, doors, ports


def yard_furnish(plan, put, auto, prop, foundation, roofs, records, occupied, reserved, footprint):
    modules, corridor = plan['modules'], plan['corridor']
    walls, doors, ports = yard_partitions(plan, foundation, put, reserved)
    moon = rectangle((44, 37, 53, 53))

    def furnish_area(area, *names):
        return [auto(thing, area, near=anchor)
                for thing, anchor in zip(names, furnishing_grid(area, len(names)))]

    def install(key, *names):
        return furnish_area(modules[key], *names)

    install('control', 'PilotConsole', 'CommsConsole')
    install('medical', 'HospitalBed', 'VitalsMonitor', 'HospitalBed')
    install('guest', *(['Bed']*4), 'Bookcase')
    install('research', 'HiTechResearchBench', 'HiTechResearchBench', 'Bookcase')
    install('kitchen', 'ElectricStove', 'ElectricStove')
    install('lounge', 'ChessTable', 'HoopstoneRing', 'Bookcase', 'PlantPot', 'PlantPot')
    billiards = put('BilliardsTable', 44, 61)
    table_cells = set(billiards['cells'])
    xs, zs = zip(*table_cells)
    # The native billiards job requires a standable ring including corners.
    billiards_clearance = rectangle((min(xs)-1, min(zs)-1, max(xs)+1, max(zs)+1)) - table_cells
    assert billiards_clearance <= modules['dining']
    reserved.update(billiards_clearance)
    put('PokerTable', 47, 64)
    for x, z, facing in ((46, 65, 1), (49, 64, 3), (47, 63, 0), (48, 66, 2)):
        put('DiningChair', x, z, facing)
    for key, count in (('dining', 3), ('lounge', 2)):
        seating = modules[key] if key == 'lounge' else rectangle((49, 59, 53, 66))
        for table in furnish_area(seating, *(['Table2x2c']*count)):
            x, z = table['origin']
            for a, b, rotation in ((x-1, z, 1), (x+2, z+1, 3), (x, z-1, 0), (x+1, z+2, 2)):
                if (a, b) in seating - reserved and (a, b) not in occupied:
                    put('DiningChair', a, b, rotation)
    install('workshop', 'TableMachining', 'FabricationBench', 'ElectricSmithy', 'Shelf', 'Shelf')
    install('repair', 'TableMachining', 'ElectricTailoringBench', 'Shelf', 'Shelf')
    install('waiting', 'Table2x2c', 'DiningChair', 'DiningChair')
    for z in (25, 28):
        for x in (10, 13, 16):
            put('Shelf', x, z)

    for key, room in modules.items():
        if key.startswith('cabin-'):
            install(key, 'Bed', 'Bed')
            install(key, 'DeltaV_PrefabPawnMarker', 'DeltaV_PrefabPawnMarker')
        elif key in plan['farms']:
            things, _, _ = plan['farms'][key]
            turn = 2 if '-south-' in key else 0
            for node, (x, z) in things:
                name = node.findtext('def')
                if name in ('GravshipHull', 'Door', 'Vent', 'HiddenConduit'):
                    continue
                rotation = (int(node.findtext('rotation'))+turn) % 4
                if prop(name, 'rotatable', 'true').lower() == 'false':
                    rotation = int(node.findtext('rotation'))
                _, (dx, dz), _ = footprint(name, 0, 0, rotation)
                state = {tag: value.text for tag in ('targetTemperature', 'plantDefToGrow')
                         if (value := node.find(f'state/saveable/{tag}')) is not None}
                if name == 'Heater':
                    state['targetTemperature'] = YARD_HEATER_TARGET
                put(name, x-dx, z-dz, rotation, state=state)
        elif key.startswith('defense-'):
            gun = ('DeltaV_Weapon_HeavyMainMedium' if key.endswith('-west')
                   else 'DeltaV_Weapon_LaserMainMedium')
            left, bottom = min(room)
            auto(gun, room, near=(left, bottom))
            auto('DeltaV_ShieldGenerator', room, state={
                'DeltaV_shieldMode': 'Guard', 'DeltaV_shieldGroundRadius': 40,
                'DeltaV_regularCapacity': 750, 'DeltaV_autoRejoinPercent': 10,
                'fuel': 750, 'configuredTargetFuelLevel': 750})
            install(key, 'DeltaV_Weapon_RapidMainSmall', 'DeltaV_Weapon_PDTurret', 'DeltaV_Weapon_PDTurret')
            if key.endswith('-west'):
                rack = auto('Shelf', room, state={'allowedDefs': ['DeltaV_Propellant', 'DeltaV_SteelHead']})
                for thing, cell in zip(('DeltaV_Propellant', 'DeltaV_SteelHead'), rack['cells']):
                    put(thing, *cell, layer='items', state={'stackCount': int(prop(thing, 'stackLimit'))})

    stock_defs = ['Steel', 'Plasteel', 'ComponentIndustrial', 'ComponentSpacer', 'Chemfuel',
                  'Silver', 'MealSurvivalPack', 'MedicineIndustrial', 'RawRice',
                  'DeltaV_Propellant', 'DeltaV_SteelHead']
    dry_storage = modules['moon'] - moon
    containers = furnish_area(dry_storage, *(['DeltaV_CargoStorage']*6))
    for cabinet in containers:
        cabinet['stateOverrides']['allowedDefs'] = stock_defs
    slot_x, _, slot_z = map(int, prop('DeltaV_CargoStorage',
        "modExtensions/li[@Class='DeltaV.DefModExtension_CargoAttachments']/slots/li/cell",
        '(1,0,1)').strip('()').split(','))
    for cabinet in containers[:2]:
        x, z = cabinet['root']
        dx, dz = ((slot_x, slot_z), (slot_z, -slot_x), (-slot_x, -slot_z), (-slot_z, slot_x))[cabinet['rotation']]
        assert (x+dx, z+dz) in cabinet['cells']
        put('DeltaV_CargoTemperatureModule', x+dx, z+dz, layer='overlay')
    stock = ([('Steel', 75)]*16 + [('Plasteel', 75)]*4 + [('ComponentIndustrial', 50)]*4
             + [('ComponentSpacer', 10)]*2 + [('Chemfuel', 150)]*8 + [('MealSurvivalPack', 10)]*24
             + [('MedicineIndustrial', 25)]*4 + [('Silver', 500)]*4 + [('RawRice', 75)]*8)
    for i, (thing, count) in enumerate(stock):
        cabinet = containers[i % len(containers)]
        cell_index = (i // len(containers)) % len(cabinet['cells'])
        put(thing, *cabinet['cells'][cell_index], layer='items', state={'stackCount': count})

    terminal = put('DeltaV_TradeTerminal', 5, 61)
    delivery = put('DeltaV_CargoStorage', 9, 61)
    supply = [put('DeltaV_CargoContainer', x, z)
              for z in (32, 37) for x in (4, 6, 8, 10, 15, 17, 19, 21)]
    terminal['stateOverrides'].update(_supplyCargo=supply, _deliveryCargo=delivery)

    for _, foundations, template_roofs in plan['farms'].values():
        plan['foundationDefs'].update(foundations)
        plan.setdefault('roofDefs', {}).update(template_roofs)
    plan['foundationDefs'].update(dict.fromkeys(moon, 'DeltaV_MoonPool'))
    plan['surfaceDefs'] = {c: d for c, d in plan['foundationDefs'].items() if d == 'DeltaV_MoonPool'}
    plan['fieldRoofs'] = {c for c, d in plan['roofDefs'].items() if d == 'DeltaV_MoonPoolFieldRoof'}
    roofs.difference_update(plan['surfaceDefs'])
    furnish_area(moon, 'DeltaV_TractorBeam', 'DeltaV_EvaSuit', 'DeltaV_EvaSuit')

    for x, z in ((13, 35), (4, 36), (22, 36), (13, 27), (9, 24), (12, 44)):
        put('Heater', x, z, state={'targetTemperature': YARD_HEATER_TARGET})
    for x, z, rotation in ((12, 32, 2), (13, 24, 2), (14, 44, 0)):
        put('OxygenPump', x, z, rotation)
    for x, z in ((13, 36), (12, 27), (12, 43)):
        put('StandingLamp', x, z)
    for x, z in ((5, 34), (7, 34)):
        put('DeltaV_CargoTemperatureModule', x, z, layer='overlay')

    cabins = []
    total = sum(len(room) for room in modules.values()) + len(corridor)
    target = YARD_HEATER_TARGET
    outside = -75
    # RoomTempTracker adjusts differences above 100 C before applying thin-roof leakage.
    difference = target-outside
    roof_loss = (min(difference, 100) + 5*max(0, difference-100))*0.00005*250
    heat = float(prop('Heater', "comps/li[@Class='CompProperties_TempControl']/energyPerSecond"))
    heater_output = heat*(120-target)/100*(250/60)
    # In vacuum GenTemperature only debits 10% from the warmer room when vents transfer heat.
    connected_area = sum(len(room) for key, room in modules.items()
                         if key not in ARM_ROOMS and key not in plan['farms'])
    corridor_heaters = math.ceil((len(corridor)+0.1*connected_area)*roof_loss/heater_output)
    backup_heaters = {'control': 1, 'medical': 1, 'moon': 1}
    for key, room in [('corridor', corridor), *modules.items()]:
        anchor = center(room)
        if key == 'corridor' or key in backup_heaters:
            heaters = []
            heater_count = corridor_heaters if key == 'corridor' else backup_heaters[key]
            for _ in range(heater_count):
                near = min(room - occupied.keys() - reserved, key=lambda c: spread(c, heaters, anchor))
                heaters.append(auto('Heater', room, near=near, state={'targetTemperature': target})['root'])
            pumps = []
            for _ in range(math.ceil(total/180) if key == 'corridor' else 1):
                mounts = [(x, z, rotation) for x, z in room - occupied.keys() - reserved
                          for rotation, (dx, dz) in enumerate(FACING) if (x+dx, z+dz) in walls]
                assert mounts, (key, 'no oxygen pump mount')
                x, z, rotation = min(mounts, key=lambda c: (spread(c[:2], pumps, anchor), c[2]))
                pumps.append(put('OxygenPump', x, z, rotation)['root'])
        if key == 'corridor':
            # Edge lighting keeps fixture footprints out of the navigation spine.
            for near in ((16, 56), (36, 56), (56, 56), (76, 56), (96, 56),
                         (56, 15), (56, 97), (30, 83), (82, 83), (30, 29), (82, 29)):
                auto('StandingLamp', room, near=near)
        elif key not in plan['farms'] and key not in ARM_ROOMS:
            auto('StandingLamp', room)
        cabins.append(dict(name=key, cells=sorted(room), temperature=21))
    plan['cabins'] = cabins

    yard_power(plan, put, auto, prop, foundation, records)
    ensure_roof_support(roofs, records, occupied, reserved, put)
    assert billiards_clearance.isdisjoint(occupied), 'billiards clearance obstructed'
    return walls, doors, ports, (56, 56)


def spread(cell, existing, anchor):
    return (-min((math.dist(cell, c) for c in existing), default=0), math.dist(cell, anchor), cell)


def yard_conduits(plan, put, prop, foundation, records, occupied):
    transmitters = {c for c, r in occupied.items() if r['transmitsPower']}
    ring_wall = {n for c in plan['ring'] for n in adj(c)
                 if n in plan['walls'] and (n[0]-56)**2 + (n[1]-56)**2 > 27**2}
    spine = {(x, 56) for x in range(113)} | {(56, z) for z in range(113)}
    grid = {(56, 56)}

    def extend(target):
        parents = {c: None for c in grid}
        queue = deque(sorted(grid))
        while queue:
            cell = queue.popleft()
            if cell in target:
                while cell not in grid:
                    grid.add(cell)
                    cell = parents[cell]
                return
            for neighbor in adj(cell):
                if neighbor in routing_cells and neighbor not in parents:
                    parents[neighbor] = cell
                    queue.append(neighbor)
        raise ValueError('No route to a station power connection')

    farm_wires = {cell for things, _, _ in plan['farms'].values()
                  for node, cell in things if node.findtext('def') == 'HiddenConduit'}
    farm_interior = set().union(*(plan['modules'][key] for key in plan['farms']))
    assert farm_wires.isdisjoint(farm_interior)
    routing_cells = foundation - farm_interior
    for c in sorted((spine & foundation) | ring_wall | farm_wires):
        extend({c})
    for room in components(transmitters):
        extend(room)
        grid.update(room)
    for record in records:
        if not record['powerWatts'] or record['transmitsPower']:
            continue
        x, z = record['root']
        if prop(record['name'], 'building/isAttachment', 'false').lower() == 'true':
            dx, dz = FACING[record['rotation']]
            x, z = x+dx, z+dz
        extend(rectangle((x-6, z-6, x+6, z+6)))
    assert grid <= foundation and len(components(grid)) == 1
    for x, z in sorted(grid-transmitters):
        put('HiddenConduit', x, z, layer='conduit')


def ensure_roof_support(roofs, records, occupied, reserved, put):
    offsets = {(x, z) for x in range(-6, 7) for z in range(-6, 7) if x*x+z*z <= 6.9**2}
    def cover(cell):
        x, z = cell
        return {(x+dx, z+dz) for dx, dz in offsets}

    holders = {tuple(c) for r in records if r['holdsRoof'] for c in r['cells']}
    unsupported = roofs - set().union(*(cover(c) for c in holders))
    while unsupported:
        choices = cover(min(unsupported)) & roofs - occupied.keys() - reserved
        support = min(choices, key=lambda c: (-len(cover(c) & unsupported), c))
        put('GravshipHull', *support)
        unsupported.difference_update(cover(support))


def yard_power(plan, put, auto, prop, foundation, records):
    # Budget covers simultaneous shield recharge as well as the Def nameplate loads.
    demand = sum(max(0, r['powerWatts']) for r in records) + 4000
    panel_watts = -float(prop('SolarGenerator', "comps/li[@Class='CompProperties_Power']/basePowerConsumption"))
    battery_comp = "comps/li[@Class='DeltaV.CompProperties_SwitchableBattery']/"
    capacity = float(prop('DeltaV_SafetyBattery', battery_comp+'storedEnergyMax'))
    efficiency = float(prop('DeltaV_SafetyBattery', battery_comp+'efficiency'))
    budget = solar_budget(demand, panel_watts, capacity, efficiency, panel_step=4)
    room = plan['modules']['power']
    standby = 0
    while standby < demand:
        reactor = auto('DeltaV_ShipReactor', room, state={'fuel': 120, 'configuredTargetFuelLevel': 120,
                       'switchOn': 'False', 'wantSwitchOn': 'False'})
        standby -= reactor['powerWatts']
    for _ in range(budget['batteries']):
        auto('DeltaV_SafetyBattery', room, state={'storedPower': capacity})

    def symmetric(cells):
        return {(a, b) for x, z in cells for a in (x, 112-x) for b in (z, 112-z)}

    entrance = (53, 7)
    blocked = symmetric(foundation)
    slots = [(x, z) for x in range(49, 2, -5) for z in (3, 8, 13)
             if rectangle((x, z, x+3, z+3)).isdisjoint(blocked)]
    count = budget['panels']//4
    selected = sorted(slots, key=lambda c: (abs(c[0]+1.5-entrance[0]) + abs(c[1]+1.5-entrance[1]), c))[:count]
    assert len(selected) == count, ('insufficient solar slots', entrance, count)
    deck = set().union(*(rectangle((x-1, z-1, x+4, z+4)) for x, z in selected)) - blocked
    assert entrance in deck and len(components(deck)) == 1, ('inaccessible solar deck', entrance)
    open_deck = symmetric(deck)
    foundation.update(open_deck)
    for x, z in selected:
        for a in (x, 109-x):
            for b in (z, 109-z):
                put('SolarGenerator', a, b)
    plan['noRoofCells'] = open_deck
    plan['solarBudget'] = dict(budget, designLoadW=demand, standbyW=standby)

def furnish(name,plan,place,prop,foundation,roofs,records,occupied,*,footprint=None):
    reserved=set(); placements=defaultdict(list)
    def put(n,x,z,rot=0,**kw):
        r=place(n,x,z,rot,**kw);placements[n].append(r)
        if r['hasInteraction']:
            ix,_,iz=r['interaction'];dx,dz=[(ix,iz),(iz,-ix),(-ix,-iz),(-iz,ix)][rot]
            reserved.add((r['root'][0]+dx,r['root'][1]+dz))
        return r
    def dims(n,rot):
        import re
        s=list(map(int,re.findall(r'-?\d+',prop(n,'size','(1,1)'))))
        return s if rot%2==0 else s[::-1]
    def auto(n,area,near=None,rotations=(0,1,2,3),state=None,margin=0):
        area=set(area);near=near or (sum(x for x,z in area)/len(area),sum(z for x,z in area)/len(area))
        candidates=sorted(area,key=lambda c:((c[0]-near[0])**2+(c[1]-near[1])**2,c))
        import re
        offset=list(map(int,re.findall(r'-?\d+',prop(n,'interactionCellOffset','(0,0,0)'))))
        has=prop(n,'hasInteractionCell','false').lower()=='true'
        for x,z in candidates:
            for rot in rotations:
                w,h=dims(n,rot);cells={(x+a,z+b) for a in range(w) for b in range(h)}
                if not cells<=area or cells&occupied.keys() or cells&reserved:continue
                halo={(x+a,z+b) for a in range(-margin,w+margin) for b in range(-margin,h+margin)}
                if not halo<=area:continue
                rx=x+(w-1)//2+(1 if w%2==0 and rot in (2,3) else 0)
                rz=z+(h-1)//2+(1 if h%2==0 and rot in (1,2) else 0)
                ix,_,iz=offset;dx,dz=[(ix,iz),(iz,-ix),(-ix,-iz),(-iz,ix)][rot]
                inter=(rx+dx,rz+dz)
                if has and (inter not in area or inter in occupied or (inter in cells and prop(n,'passability','Standable')=='Impassable')):continue
                # Keep at least one usable maintenance edge for non-walkable machines.
                if not any(c in area and c not in occupied and c not in cells for a in cells for c in adj(a)):continue
                return put(n,x,z,rot,state=state)
        raise ValueError(f'No placement for {n}, area={len(area)}, near={near}')

    if name=='PrototypeShip':
        engines=[]
        for x in (4,11):engines.append(put('LargeThruster',x,0))
        for x in (1,14):engines.append(put('LargeThruster',x,25,2))
        for z in (9,22):
            engines.extend((put('SmallThruster',0,z,1),put('SmallThruster',15,z,3)))
        engine_cells={tuple(c) for r in engines for c in r['cells']};roofs-=engine_cells
        walls={c for c in foundation if any(n not in foundation for n in adj(c))}-engine_cells
        walls|={(x,z) for z in (13,19,24) for x in range(1,16) if x not in (7,8,9)}
        walls|={(x,z) for x in (7,9) for z in range(19,24)}
        ports={(0,18):3,(16,18):1}
        doors={(7,22),(9,22),(4,19),(12,19),(4,13),(12,13)}
        walls-=doors|ports.keys()
        for x,z in sorted(walls):put('GravshipHull',x,z)
        for x,z in sorted(doors):put('Door',x,z)
        for (x,z),rot in ports.items():put('DeltaV_DockingPort',x,z,rot)
        start=(8,18)
        put('PilotConsole',7,26)
        put('Bed',2,20);put('Bed',5,20)
        put('Table2x2c',11,20)
        put('DiningChair',10,20,1);put('DiningChair',13,21,3)
        for x in (2,5,10,13):
            r=put('DeltaV_CargoStorage',x,14,state={'allowedDefs':['Steel','ComponentIndustrial','ComponentSpacer','Chemfuel','MealSurvivalPack','MealSimple','MedicineIndustrial','Silver','RawRice']})
            if x==13:put('DeltaV_CargoTemperatureModule',r['root'][0]+1,r['root'][1]+1,layer='overlay')
        put('GravEngine',7,9)
        put('ChemfuelTank',12,8,state={'fuel':150,'configuredTargetFuelLevel':150})
        put('ChemfuelPoweredGenerator',2,8,state={'fuel':30,'configuredTargetFuelLevel':30})
        put('ChemfuelPoweredGenerator',2,11,state={'fuel':30,'configuredTargetFuelLevel':30})
        put('ChemfuelPoweredGenerator',11,11,state={'fuel':30,'configuredTargetFuelLevel':30})
        put('Battery',6,11);put('Battery',13,11)
        put('OrbitalTradeBeacon',10,17)
        # Transparent sealed field roof supplies daylight; no ordinary roof or grow lamp.
        moon={(x,z) for x in range(3,14) for z in range(2,8)}
        plan['surfaceDefs']={c:'DeltaV_MoonPool' for c in moon}
        for c in moon:plan['foundationDefs'][c]='DeltaV_MoonPool';roofs.discard(c)
        for x in (3,5,7,9,11,13):
            basin=put('HydroponicsBasin',x,3,state={'plantDefToGrow':'Plant_Rice'})
            for c in basin['cells']:put('Plant_Rice',*c,layer='plants',state={'growth':0.65})
        put('ElectricStove',5,8,1)
        # Supplies use ordinary on-map stacks in the actual ASF storage cells.
        for n,x,z,count in [('Steel',2,10,75),('ComponentIndustrial',2,11,12),('Chemfuel',5,10,150),
                            ('MealSurvivalPack',13,10,10),('MealSurvivalPack',14,10,10),('MealSurvivalPack',14,11,10),
                            ('MedicineIndustrial',13,11,10),('Silver',10,10,100)]:
            put(n,x,z+4,layer='items',state={'stackCount':count})
        habitable=foundation-walls-engine_cells-set(ports)-doors
        # Each enclosed room gets its own pump, heater and light. Equipment is placed before
        # the pumps so all rotations can be checked against the actual mounting wall.
        for room in components(habitable):
            if len(room)<3:continue
            room_reserved=reserved&room
            if room<=reserved:reserved.difference_update(room)
            available=room-occupied.keys()-reserved
            candidates=[]
            for x,z in available:
                for rot,(dx,dz) in enumerate(((0,1),(1,0),(0,-1),(-1,0))):
                    if (x+dx,z+dz) in walls:candidates.append((x,z,rot))
            if not candidates:
                # The ring corridor is supplied from a mounting wall beside a compartment.
                raise ValueError(f'No oxygen mounting cell in room of {len(room)} cells, seed={min(room)}')
            x,z,rot=min(candidates);put('OxygenPump',x,z,rot)
            auto('Heater',room,state={'targetTemperature':21})
            auto('StandingLamp',room)
            reserved.update(room_reserved)
    else:
        walls, doors, ports, start = yard_furnish(plan, put, auto, prop, foundation, roofs,
                                                  records, occupied, reserved, footprint)
    plan['walls']=walls;plan['doors']=doors;plan['ports']=ports
    if name == 'Shipyard':
        yard_conduits(plan, put, prop, foundation, records, occupied)
    else:
        transmitting={tuple(c) for r in records if r['transmitsPower'] for c in r['cells']}
        for x,z in sorted(foundation-transmitting):put('HiddenConduit',x,z,layer='conduit')
    return walls,start

def prepare_state(r,state,field,prop):
    import xml.etree.ElementTree as ET
    name=r['name']
    # Resolve current health and material rather than importing wear from a live sample.
    if r['category']=='Building':
        hp=float(prop(name,'statBases/MaxHitPoints','100'))
        if r['stuff']:
            hp=hp*float(prop(r['stuff'],'stuffProps/statFactors/MaxHitPoints','1'))+float(prop(r['stuff'],'stuffProps/statOffsets/MaxHitPoints','0'))
        elif state.find('stuff') is not None:state.remove(state.find('stuff'))
        field(state,'health',round(hp))
    if name == 'DeltaV_TradeTerminal':
        prepare_trade_state(r, state, field, indiscriminate_supply=False)
    if name in ('DeltaV_Weapon_RapidMainSmall', 'DeltaV_Weapon_HeavyMainMedium'):
        for kind,tag in [('Heads','headSupply'),('Propellant','propellantSupply')]:
            node=state.find(tag)
            if node is None:
                node=ET.SubElement(state,tag)
            capacity=prop(name,"comps/li[@Class='DeltaV.CompProperties_GunSupply'][kind='"+kind+"']/fuelCapacity")
            field(node,'fuel',capacity);field(node,'configuredTargetFuelLevel',capacity);field(node,'allowAutoRefuel','True')
    if r['category']=='Item':
        count=int(state.findtext('stackCount','1'))
        assert 0<count<=int(prop(name,'stackLimit','1')),(name,'invalid stack',count)


def validate_and_draw(name,plan,records,foundation,roofs,reachable,here,generated):
    assert len(components(foundation))==1
    sealed={tuple(c) for r in records if r['airtight'] or r['name']=='Vent' for c in r['cells']}
    air=foundation-sealed-plan.get('noRoofCells',set())
    assert all(n in foundation for c in air for n in adj(c)),(name,'open hull boundary')
    assert air<=roofs|plan.get('surfaceDefs',{}).keys(),(name,'unroofed interior')
    counts=Counter(r['name'] for r in records)
    for r in records:
        if r['name']=='DeltaV_PrefabPawnMarker':assert tuple(r['root']) in reachable
    yard_report = {}
    if name=='Shipyard':
        yard_report = validate_yard(plan, records, counts, foundation, roofs, reachable)
    else:
        assert counts['GravEngine']==1 and counts['Bed']==2 and counts['DeltaV_CargoStorage']==4
        assert not any(r['name'].startswith('DeltaV_Weapon') for r in records)
        engine=next(r for r in records if r['name']=='GravEngine')
        assert len(foundation)<=500 and all(math.dist(c,engine['root'])<=18.9 for c in foundation)
        assert counts['SmallThruster']<=4 and counts['LargeThruster']<=6
        assert counts['HydroponicsBasin']==6 and counts['Plant_Rice']==24 and counts['SunLamp']==0
        assert all(tuple(c) in plan['surfaceDefs'] and tuple(c) not in roofs
                   for r in records if r['name']=='HydroponicsBasin' for c in r['cells'])
    generated_power=-sum(min(0,r['powerWatts']) for r in records
                         if r['stateOverrides'].get('switchOn') != 'False')
    load=sum(max(0,r['powerWatts']) for r in records)
    report={'counts':dict(sorted(counts.items())),'foundationCells':len(foundation),'roofCells':len(roofs),
            'generationW':generated_power,'nameplateLoadW':load,'solarBudget':plan.get('solarBudget'),
            'stage':'offline; no game SpawnSetup',
            'foundation':[list(c) for c in sorted(foundation)],'roofs':[list(c) for c in sorted(roofs)],
            **yard_report}
    (generated/(name+'.layout.json')).write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    assert generated_power>=load+(4000 if name=='Shipyard' else 0),(name,'power deficit',generated_power,load)
    w,h=plan['size'];s=16 if name=='Shipyard' else 40;pad=80
    im=Image.new('RGB',(w*s+pad*2+620,h*s+pad*2+100),'#101c29');d=ImageDraw.Draw(im)
    def f(n):return ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',n)
    def box(c):x,z=c;return(pad+x*s,pad+(h-1-z)*s,pad+(x+1)*s,pad+(h-z)*s)
    for c in sorted(foundation):
        zone=plan['zones'].get(c,-1);color=COLORS[zone] if zone>=0 else '#3e5360'
        if c in plan['foundationDefs']:color='#397783'
        if c in plan.get('noRoofCells',()):color='#202e3f'
        d.rectangle(box(c),fill=color,outline='#455866')
    colors={'GravshipHull':'#81949f','Door':'#dac397','DeltaV_DockingPort':'#f5d58d',
            'Bed':'#a2b7de','HydroponicsBasin':'#9fd2a2','Plant_Rice':'#80b786','DeltaV_CargoStorage':'#e1b674',
            'DeltaV_TradeTerminal':'#6de6cc','DeltaV_CargoContainer':'#bb9563',
            'GravEngine':'#e5be76','GravcorePowerCell':'#cfb6de','Battery':'#bea0ce',
            'SolarGenerator':'#4269a8','DeltaV_SafetyBattery':'#a1b4cd','DeltaV_ShipReactor':'#bd8077',
            'DeltaV_PrefabPawnMarker':'#eddee8','OxygenPump':'#78d1da','Vent':'#85cde8',
            'Heater':'#d49f7d','DeltaV_Weapon_HeavyMainMedium':'#af8d85',
            'DeltaV_Weapon_LaserMainMedium':'#cc7c72'}
    for r in records:
        if r['layer'] in ('conduit','items'):continue
        xs,zs=zip(*r['cells']);a=box((min(xs),max(zs)));b=box((max(xs),min(zs)))
        rect=(a[0]+1,a[1]+1,b[2]-1,b[3]-1)
        d.rectangle(rect,fill=colors.get(r['name'],'#c6b6a2'),outline='#182535',width=1)
        if r['name']=='DeltaV_PrefabPawnMarker':d.ellipse(rect,fill='#f5e1e9')
    if name == 'Shipyard':
        wires = {tuple(r['root']) for r in records if r['name'] == 'HiddenConduit'}
        power_cells = {tuple(c) for r in records if r['transmitsPower'] for c in r['cells']}
        def midpoint(cell):
            left, top, right, bottom = box(cell)
            return ((left+right)//2, (top+bottom)//2)
        for cell in sorted(wires):
            x0, z0 = cell
            for neighbor in ((x0+1, z0), (x0, z0+1)):
                if neighbor in wires:
                    d.line((midpoint(cell), midpoint(neighbor)), fill='#c9aa63', width=1)
            for neighbor in adj(cell):
                if neighbor in power_cells:
                    start, end = midpoint(cell), midpoint(neighbor)
                    edge = ((start[0]+end[0])//2, (start[1]+end[1])//2)
                    d.line((start, edge), fill='#c9aa63', width=1)
    d.text((pad,18),'星界边境 · 环形船厂' if name=='Shipyard' else '教程赠船 · 无武器货船',font=f(33),fill='#ecf2f5')
    x=w*s+pad+35;y=80
    d.text((x,y),f'{w} × {h} 格  /  北向朝上',font=f(27),fill='#edf0f3');y+=55
    d.text((x,y),'建筑脚印与输出 prefab 同源',font=f(23),fill='#b2c3ce');y+=65
    keynames={'Bed':'床','HospitalBed':'病床','DeltaV_PrefabPawnMarker':'常驻乘员',
              'HydroponicsBasin':'水培盆','Vent':'通风口','Heater':'加热器','DeltaV_CargoStorage':'仓储柜',
              'DeltaV_CargoContainer':'锁定供货柜',
              'DeltaV_ShieldGenerator':'护盾','DeltaV_Weapon_PDTurret':'点防',
              'DeltaV_Weapon_RapidMainSmall':'小型速射炮','OxygenPump':'供氧泵',
              'DeltaV_Weapon_HeavyMainMedium':'中型重炮','DeltaV_Weapon_LaserMainMedium':'中型激光炮',
              'GravcorePowerCell':'逆重电芯','Battery':'电池','LargeThruster':'主推进器',
              'SolarGenerator':'太阳能板','DeltaV_SafetyBattery':'安全储备电池','DeltaV_ShipReactor':'裂变后备',
              'SmallThruster':'小推进器','GravEngine':'逆重引擎','ChemfuelPoweredGenerator':'化燃发电机'}
    for key,label in keynames.items():
        if counts[key]:d.text((x,y),f'{label}  {counts[key]}',font=f(24),fill='#d1dce4');y+=38
    y+=20
    for line in [f'发电 {generated_power/1000:.1f} kW',f'铭牌用电 {load/1000:.1f} kW','护盾回充、瞬时开火另计','黄色门：对接 / 通行','浅色圆点：乘员生成位置','蓝绿色地面：透光力场顶']:
        d.text((x,y),line,font=f(22),fill='#a7bdcc');y+=36
    if name == 'Shipyard':
        d.text((x, y-2), f"金线：电网 · 暗线 {counts['HiddenConduit']} 格", font=f(22), fill='#c9aa63')
        d.text((x, y+28), f'基架 {len(foundation):,} 格   实体屋顶 {len(roofs):,} 格', font=f(22), fill='#edf0f3')
        steps = yard_report['westBerthSteps']
        d.text((x, y+64), f"西泊口 → 终端 {steps['terminal']} 步 / 取货 {steps['delivery']} 步",
               font=f(22), fill='#6de6cc')
        for index, title in enumerate(NAMES):
            legend_y = y+114+index*34
            d.rectangle((x, legend_y, x+19, legend_y+19), fill=COLORS[index])
            d.text((x+31, legend_y-5), title, font=f(22), fill='#edf0f3')
        for title, cell in [('西泊口', (1, 56)), ('东泊口', (111, 56)), ('南泊口', (56, 1)),
                            ('交易', (5, 66)), ('工坊', (106, 65)), ('月池', (49, 44)),
                            ('常规仓储', (40, 49)), ('锁定供货仓', (13, 42))]:
            left, top, right, bottom = box(cell)
            point = ((left+right)/2, (top+bottom)/2)
            d.rectangle(d.textbbox(point, title, font=f(19), anchor='mm'), fill='#182535')
            d.text(point, title, font=f(19), anchor='mm', fill='#edf0f3')
    d.text((pad,h*s+pad+25),'已布置建筑、人员标记和补给；游戏内生成、通行及存读档待验收。',font=f(24),fill='#a9bdcd')
    im.save(here/(name+'.png'))


def validate_yard(plan, records, counts, foundation, roofs, reachable):
    expected = {'DeltaV_PrefabPawnMarker': 24, 'Bed': 28, 'HospitalBed': 2,
                'HydroponicsBasin': 120, 'SunLamp': 0, 'Plant_Rice': 0,
                'DeltaV_CargoStorage': 7, 'DeltaV_CargoContainer': 16, 'DeltaV_TradeTerminal': 1,
                'TableMachining': 2, 'FabricationBench': 1, 'ElectricTailoringBench': 1,
                'ElectricSmithy': 1, 'HiTechResearchBench': 2, 'DeltaV_TractorBeam': 1,
                'DeltaV_EvaSuit': 2, 'DeltaV_ShieldGenerator': 4, 'DeltaV_Weapon_RapidMainSmall': 4,
                'DeltaV_Weapon_HeavyMainMedium': 2, 'DeltaV_Weapon_LaserMainMedium': 2,
                'DeltaV_Weapon_PDTurret': 8, 'BilliardsTable': 1, 'PokerTable': 1}
    assert all(counts[thing] == count for thing, count in expected.items()), (expected, counts)
    assert not counts['GravEngine'] and not counts['LargeThruster']
    for key, (things, foundations, template_roofs) in plan['farms'].items():
        turn = 2 if '-south-' in key else 0
        expected = Counter((node.findtext('def'), cell, (int(node.findtext('rotation')) + turn) % 4
                            if node.findtext('def') in ('Door', 'Vent', 'HydroponicsBasin') else 0)
                           for node, cell in things)
        actual = Counter((r['name'], tuple(r['root']), r['rotation'])
                         for r in records if tuple(r['root']) in foundations
                         and r['name'] in {name for name, _, _ in expected})
        # The station may add power leads on the module's perimeter.
        assert all(actual[entry] == count for entry, count in expected.items()), (key, expected - actual)
        assert all(plan['roofDefs'][c] == d for c, d in template_roofs.items())
    assert all(set(r['cells']) <= plan['surfaceDefs'].keys() - roofs
               for r in records if r['name'] == 'HydroponicsBasin')
    rooms = {'corridor': plan['corridor'], **plan['modules']}
    air_cells = {c: key for key, room in rooms.items() for c in room}
    vent_counts = Counter()
    for vent in plan['vents']:
        cell = vent['cell']
        peer, room = vent['rooms']
        assert 'corridor' in (peer, room), vent
        vent_counts[room if peer == 'corridor' else peer] += 1
        assert cell in roofs and all(n in foundation - plan['noRoofCells'] for n in adj(cell)), vent
        assert {air_cells[n] for n in adj(cell) if n in air_cells} == set(vent['rooms']), vent
    assert set(vent_counts) == plan['modules'].keys() - ARM_ROOMS
    assert all(1 <= count <= 2 for count in vent_counts.values())
    assert len(foundation) < 7949 and len(roofs) <= 5000, ('yard area', len(foundation), len(roofs))
    bodies = [r for r in records if r['layer'] == 'body']
    panels = {c for r in bodies if r['name'] == 'SolarGenerator' for c in r['cells']}
    for cells in (panels, plan['noRoofCells']):
        assert {(112-x, z) for x, z in cells} == cells, 'solar arrays must mirror east to west'
        assert {(x, 112-z) for x, z in cells} == cells, 'solar arrays must mirror north to south'
    assert counts['SolarGenerator'] == plan['solarBudget']['panels']
    for z in (7, 105):
        assert {(54, z), (58, z)} <= plan['doors']
        assert {(53, z), (59, z)} <= plan['noRoofCells'] & reachable
    assert all(r['stateOverrides']['targetTemperature'] == YARD_HEATER_TARGET
               for r in records if r['name'] == 'Heater')
    solid = {c for r in bodies if r['passability'] == 'Impassable'
             and r['name'] not in ('Door', 'DeltaV_DockingPort') for c in r['cells']}
    for r in bodies:
        if r['name'] not in ('GravshipHull', 'Door', 'Vent', 'DeltaV_DockingPort'):
            assert any(n in reachable and n not in r['cells'] for c in r['cells'] for n in adj(c)), (
                'no maintenance approach', r['name'], r['root'])

    # Count four-way walking steps to a cardinal touch cell within the pressure hull.
    start = (1, 56)
    walkable = foundation - plan['noRoofCells'] - solid
    assert {(56, z) for z in range(1, 112)} <= plan['corridor'] & walkable, 'blocked north-south spine'
    distances, queue = {start: 0}, deque([start])
    while queue:
        c = queue.popleft()
        for n in adj(c):
            if n in walkable and n not in distances:
                distances[n] = distances[c]+1
                queue.append(n)
    assert {(111, 56), (56, 1), (56, 56)} <= distances.keys(), 'disconnected pressurized berths'
    terminal = next(r for r in records if r['name'] == 'DeltaV_TradeTerminal')
    delivery = terminal['stateOverrides']['_deliveryCargo']
    assert all(set(r['cells']) <= plan['modules']['moon'] - plan['surfaceDefs'].keys()
               for r in records if r['name'] == 'DeltaV_CargoStorage' and r is not delivery)
    steps = {}
    for key, r, limit in (('terminal', terminal, 12), ('delivery', delivery, 18)):
        steps[key] = min(distances[n] for c in r['cells'] for n in adj(c)
                         if n in distances and n not in r['cells'])
        assert steps[key] <= limit, (key, 'west berth walk', steps[key], limit)
    assert ARM_CUT in distances and ARM_CUT_CELLS <= foundation, 'unusable arm neck'
    assert all((ARM_CUT[0]+dx, ARM_CUT[1]) not in foundation
               for dx in (-5, -4, -3, -2, 2, 3, 4, 5)), 'cutting approach obstructed'
    blocks = components(foundation - ARM_CUT_CELLS)
    assert len(blocks) == 2, 'cut must separate exactly one storage arm'
    arm = next(block for block in blocks if (13, 40) in block)
    station = next(block for block in blocks if block is not arm)
    assert plan['modules']['cargo'] | plan['modules']['stores'] <= arm
    assert set(plan['ports']) | plan['noRoofCells'] | plan['surfaceDefs'].keys() <= station
    for r in records:
        if r['name'] in ('GravshipHull', 'HiddenConduit'):
            continue
        cells = set(r['cells'])
        if r['name'] == 'DeltaV_CargoContainer':
            assert cells <= arm, ('locked supply left behind', r['root'])
        elif r['name'] in ('PilotConsole', 'DeltaV_TradeTerminal',
                           'DeltaV_CargoStorage', 'DeltaV_SafetyBattery', 'DeltaV_ShipReactor'):
            assert cells <= station, ('station equipment detached with warehouse', r['name'])
        else:
            assert cells <= station or cells <= arm, ('fixture on the cut', r['name'], r['root'])
    return dict(size=plan['size'], cabins=plan['cabins'], vents=plan['vents'], westBerthSteps=steps,
                storageArm={'cutCells': sorted(ARM_CUT_CELLS), 'detachedFoundationCells': len(arm)})
