"""Pressurized modules and circulation for the three civilian sector stations."""
import math
from station_layout import FACING, center, furnishing_grid, rectangle, solar_budget


# Rectangles describe interior cells; the shell adds one wall cell around each.
# Each module meets a pressurized corridor across a one-cell bulkhead.
STATIONS = {
    'SectorTradeStation': dict(crew=12, ports=((40, 9, 2), (82, 19, 1)),
        solar=((67, 37, 116, 61),), solarDoor=(66, 50, 1),
        corridors=((30, 37, 58, 41), (30, 17, 58, 21), (30, 17, 34, 41), (54, 17, 58, 41),
                   (38, 10, 43, 17), (59, 17, 81, 21), (11, 9, 14, 53), (14, 18, 29, 21)),
        links=(('kitchen', 'dining'), ('receiving', 'bonded')),
        modules={
            'power': (61, 5, 74, 15), 'medical': (24, 9, 35, 15),
            'bonded': (75, 23, 84, 34), 'receiving': (62, 23, 73, 34),
            'exchange': (33, 43, 51, 53), 'lounge': (45, 6, 56, 15),
            'farm-west': (16, 43, 28, 55), 'farm-east': (53, 43, 65, 55),
            'dining': (20, 30, 28, 40), 'kitchen': (16, 23, 28, 28),
            'residence-a': (3, 9, 9, 14), 'residence-b': (3, 16, 9, 21),
            'residence-c': (3, 23, 9, 28), 'residence-d': (3, 30, 9, 35),
            'residence-e': (3, 37, 9, 42), 'residence-f': (16, 9, 22, 16),
        }),
    'SectorShipyard': dict(crew=14, ports=((43, 30, 0),),
        solar=((89, 26, 138, 50),), solarDoor=(88, 31, 1),
        corridors=((4, 20, 87, 24), (16, 20, 20, 59), (69, 20, 73, 68), (41, 24, 45, 29)),
        links=(('kitchen', 'lounge'),),
        modules={
            'logistics': (4, 6, 17, 18), 'fabrication': (19, 6, 32, 18),
            'kitchen': (34, 8, 46, 18), 'lounge': (48, 6, 59, 18),
            'power': (61, 6, 73, 18), 'control': (75, 6, 87, 18),
            'farm-west': (2, 26, 14, 38), 'farm-east': (75, 26, 87, 38),
            'residence-a': (7, 40, 14, 45), 'residence-b': (7, 47, 14, 52),
            'residence-c': (6, 54, 14, 59), 'residence-d': (75, 40, 82, 45),
            'residence-e': (75, 47, 82, 52), 'residence-f': (75, 54, 83, 59),
            'repair': (75, 61, 87, 71),
        }),
    'SectorFactoryStation': dict(crew=14, ports=((17, 7, 2),),
        solar=((29, 83, 78, 107),), solarDoor=(47, 82, 0),
        corridors=((15, 8, 19, 43), (15, 39, 49, 43),
                   (45, 39, 49, 81), (45, 66, 75, 70)),
        links=(('kitchen', 'dining'),),
        modules={
            'power': (2, 9, 13, 20), 'logistics': (2, 22, 13, 36),
            'machining': (21, 9, 34, 22), 'casting': (21, 24, 35, 37),
            'dispatch': (37, 25, 49, 37), 'farm-west': (16, 45, 28, 57),
            'farm-east': (30, 45, 42, 57),
            'residence-a': (36, 59, 43, 64), 'residence-b': (36, 66, 43, 72),
            'residence-c': (35, 74, 43, 80), 'residence-d': (51, 72, 57, 77),
            'residence-e': (59, 72, 65, 77), 'residence-f': (67, 72, 75, 77),
            'kitchen': (51, 46, 62, 55), 'dining': (51, 57, 65, 64),
            'lounge': (67, 54, 75, 64), 'control': (77, 63, 88, 73),
        }),
}

BASINS_PER_FARM = 14
SUPPLIES = ['Steel', 'Plasteel', 'ComponentIndustrial', 'ComponentSpacer',
            'Chemfuel', 'Silver', 'MealSurvivalPack', 'MedicineIndustrial', 'RawRice']


def neighbors(cell):
    x, z = cell
    return {(x+dx, z+dz) for dx, dz in FACING}


def shape(name):
    spec = STATIONS[name]
    modules = {key: rectangle(bounds) for key, bounds in spec['modules'].items()}
    corridor = set().union(*(rectangle(bounds) for bounds in spec['corridors']))
    floor = set(corridor)
    for key, room in modules.items():
        assert not floor & room and all(not neighbors(c) & floor for c in room), (name, key, 'missing bulkhead')
        floor.update(room)
    cells = {(x+dx, z+dz) for x, z in floor for dx in (-1, 0, 1) for dz in (-1, 0, 1)}
    solar = set().union(*(rectangle(bounds) for bounds in spec['solar']))
    assert not cells & solar
    cells |= solar
    return dict(size=(max(x for x, _ in cells)+1, max(z for _, z in cells)+1),
                cells=cells, modules=modules, corridor=corridor, noRoofCells=solar)


def install_solar(name, plan, foundation, records, reserved, put, auto, prop, battery_room, mount, outside):
    demand = sum(max(0, r['powerWatts']) for r in records)
    battery_comp = "comps/li[@Class='CompProperties_Battery']/"
    capacity = float(prop('Battery', battery_comp+'storedEnergyMax'))
    efficiency = float(prop('Battery', battery_comp+'efficiency'))
    panel_watts = -float(prop('SolarGenerator', "comps/li[@Class='CompProperties_Power']/basePowerConsumption"))
    budget = solar_budget(demand, panel_watts, capacity, efficiency)
    solar = plan['noRoofCells']
    x0, z0 = min(x for x, _ in solar), min(z for _, z in solar)
    slots = [(x, z) for x in range(x0+1, max(x for x, _ in solar), 5)
             for z in range(z0+1, max(z for _, z in solar), 5)
             if rectangle((x, z, x+3, z+3)) <= solar
             and not rectangle((x, z, x+3, z+3)) & reserved]
    assert len(slots) >= budget['panels'], (name, budget, len(slots))
    active_deck = {outside}
    for x, z in sorted(slots, key=lambda c: (math.dist(c, mount), c))[:budget['panels']]:
        put('SolarGenerator', x, z)
        active_deck.update(rectangle((x-1, z-1, x+4, z+4)) & solar)
    # Keep only the serviced panel aprons. They meet on their one-cell aisles.
    assert any(c in active_deck for c in neighbors(outside)), (name, 'solar access disconnected')
    foundation.difference_update(solar - active_deck)
    plan['noRoofCells'] = active_deck
    for _ in range(budget['batteries']):
        auto('Battery', battery_room, near=center(battery_room), state={'storedPower': capacity})
    plan['solarBudget'] = budget


def furnish(name, plan, foundation, roofs, records, occupied, reserved, put, auto, prop, life_support, finish_structure):
    spec = STATIONS[name]
    modules, corridor = plan['modules'], plan['corridor']
    solar = plan['noRoofCells']
    roofs.difference_update(solar)
    interior = corridor | set().union(*modules.values())
    walls = foundation - solar - interior
    starts = []
    for px, pz, rot in spec['ports']:
        dx, dz = FACING[rot]
        start = (px-dx, pz-dz)
        assert (px, pz) in walls and start in corridor and (px+dx, pz+dz) not in foundation, (name, 'docking approach')
        walls.remove((px, pz))
        starts.append(start)
    sx, sz, face = spec['solarDoor']
    dx, dz = FACING[face]
    assert (sx, sz) in walls and (sx-dx, sz-dz) in interior and (sx+dx, sz+dz) in solar
    walls.remove((sx, sz))
    reserved.update(((sx-dx, sz-dz), (sx+dx, sz+dz)))
    extra_doors = [spec['solarDoor']]
    doors = {}

    def connect(room, other, label):
        middle = center(room)
        candidates = [(math.dist((x, z), middle), x, z, face)
                      for x, z in walls for face, (dx, dz) in enumerate(FACING)
                      if (x+dx, z+dz) in other and (x-dx, z-dz) in room]
        assert candidates, (name, label, 'no door between rooms')
        _, x, z, face = min(candidates)
        walls.remove((x, z))
        return (x, z, face)

    for key, room in modules.items():
        doors[key] = connect(room, corridor, key)
    for a, b in spec['links']:
        x, z, face = connect(modules[a], modules[b], (a, b))
        dx, dz = FACING[face]
        reserved.update(((x+dx, z+dz), (x-dx, z-dz)))
        extra_doors.append((x, z, face))
    for x, z in sorted(walls):
        put('GravshipHull', x, z)
    for x, z, face in (*doors.values(), *extra_doors):
        put('Door', x, z, face)
    for port in spec['ports']:
        put('DeltaV_DockingPort', *port)
    reserved.update(c for c in corridor if neighbors(c) <= corridor)
    reserved.update(starts)

    for key, room in modules.items():
        x, z, face = doors[key]
        dx, dz = FACING[face]
        reserved.add((x+dx, z+dz))
        # Continue the corridor through the room entrance and past its center.
        cell = (x-dx, z-dz)
        while cell in room:
            reserved.add(cell)
            cell = (cell[0]-dx, cell[1]-dz)
        if not key.startswith('farm-'):
            life_support(room, walls, center(room), key)
        auto('StandingLamp', room, near=(x-dx, z-dz))
    life_support(corridor, walls, center(corridor), 'corridor')

    def install(key, *things):
        area = modules[key]
        for thing, anchor in zip(things, furnishing_grid(area, len(things))):
            auto(thing, area, near=anchor)

    def stores(key, count):
        room = modules[key]
        return [auto('DeltaV_CargoStorage', room, rotations=(0,), near=anchor, state={'allowedDefs': SUPPLIES})
                for anchor in furnishing_grid(room, count)]

    def stock(cabinet, thing, stack, count):
        for x, z in cabinet['cells'][:count]:
            put(thing, x, z, layer='items', state={'stackCount': stack})

    def dining(key, count):
        area = modules[key]
        for anchor in furnishing_grid(area, count):
            table = auto('Table2x2c', area, near=anchor)
            x, z = table['origin']
            for a, b, face in ((x-1, z, 1), (x+2, z+1, 3), (x, z-1, 0), (x+1, z+2, 2)):
                if (a, b) in area and (a, b) not in occupied and (a, b) not in reserved:
                    put('DiningChair', a, b, face)

    for key, room in modules.items():
        if key.startswith('residence-'):
            beds = 3 if spec['crew'] == 14 and key in ('residence-c', 'residence-f') else 2
            for anchor in furnishing_grid(room, beds):
                auto('Bed', room, near=anchor)
                auto('DeltaV_PrefabPawnMarker', room, near=anchor)
        if key.startswith('farm-'):
            # Keep the grow lamp on the aisle, then fill only its lit footprint.
            lamp = auto('SunLamp', room, near=center(room))
            lit = {c for c in room if math.dist(c, lamp['root']) <= 5.5}
            for _ in range(BASINS_PER_FARM):
                basin = auto('HydroponicsBasin', lit, rotations=(0, 1),
                             near=lamp['root'], state={'plantDefToGrow': 'Plant_Rice'})
                for x, z in basin['cells']:
                    put('Plant_Rice', x, z, layer='plants', state={'growth': 1})
            life_support(room, walls, center(room), key)

    install('lounge', 'ChessTable', 'HoopstoneRing', 'Bookcase', 'PlantPot', 'PlantPot')
    dining('lounge', 2)
    dining('dining' if 'dining' in modules else 'lounge', 4)
    install('kitchen', 'ElectricStove', 'ElectricStove')
    pantry = stores('kitchen', 3)
    # The temperature module targets the cargo inventory, leaving the kitchen warm.
    for cabinet in pantry[:2]:
        x, z = cabinet['root']
        put('DeltaV_CargoTemperatureModule', x+1, z+1, layer='overlay')
    for cabinet, (thing, stack, count) in zip(pantry, (
            ('RawRice', 75, 6), ('MealSurvivalPack', 10, 6), ('MedicineIndustrial', 10, 3))):
        stock(cabinet, thing, stack, count)

    control = 'exchange' if 'exchange' in modules else 'control'
    install(control, 'PilotConsole', 'CommsConsole')
    ward = modules.get('medical', modules[control])
    monitor = auto('VitalsMonitor', ward, near=center(ward))
    mx, mz = monitor['root']
    for dx in (-1, 1):
        auto('HospitalBed', ward, near=(mx+dx, mz))
    if name == 'SectorTradeStation':
        supply = [auto('DeltaV_CargoContainer', modules['bonded']) for _ in range(3)]
        delivery = stores('receiving', 1)[0]
        stores('receiving', 7)
        terminal = auto('DeltaV_TradeTerminal', modules['exchange'])
        terminal['stateOverrides'].update(_supplyCargo=supply, _deliveryCargo=delivery)
        dining('exchange', 3)
        install('receiving', 'OrbitalTradeBeacon')
    elif name == 'SectorShipyard':
        install('fabrication', 'TableMachining', 'FabricationBench', 'ElectricSmithy')
        install('repair', 'TableMachining', 'ElectricSmithy', 'DeltaV_EvaSuit', 'DeltaV_EvaSuit')
        cabinets = stores('logistics', 12)
        stock(cabinets[0], 'Steel', 75, 6)
        stock(cabinets[1], 'ComponentIndustrial', 25, 4)
    else:
        install('machining', 'TableMachining', 'TableMachining', 'FabricationBench')
        install('casting', 'ElectricSmithy', 'ElectricSmithy', 'ElectricTailoringBench', 'HiTechResearchBench')
        cabinets = stores('logistics', 10)
        stock(cabinets[0], 'Steel', 75, 6)
        stock(cabinets[1], 'ComponentIndustrial', 25, 4)
        stores('dispatch', 8)

    battery_room = modules['power']
    reactor = auto('DeltaV_ShipReactor', battery_room, near=center(battery_room),
                   state={'fuel': 120, 'configuredTargetFuelLevel': 120,
                          'switchOn': 'False', 'wantSwitchOn': 'False'})
    uranium = auto('DeltaV_CargoStorage', battery_room, rotations=(0,), state={'allowedDefs': ['Uranium']})
    stock(uranium, 'Uranium', 75, 2)
    assert sum(max(0, r['powerWatts']) for r in records) <= -reactor['powerWatts'], (
        name, 'reserve cannot carry full load')
    install_solar(name, plan, foundation, records, reserved, put, auto, prop,
                  battery_room, (sx, sz), (sx+FACING[spec['solarDoor'][2]][0], sz+FACING[spec['solarDoor'][2]][1]))
    finish_structure(roofs, sparse_grid=True)
    palette = {'farm': 'Structure_GreenPine', 'residence': 'Structure_Teal',
               'kitchen': 'Structure_BrownSubtle', 'dining': 'Structure_BrownSubtle',
               'lounge': 'Structure_PurpleSubtle', 'control': 'Structure_BlueSubtle',
               'exchange': 'Structure_BlueSubtle', 'medical': 'Structure_BlueIce',
               'power': 'Structure_RedSubtle'}
    plan['rooms'] = [dict(color=palette.get(key.split('-')[0], 'Structure_Mustard'), cells=room)
                     for key, room in modules.items()]
    plan['doors'] = doors
    return walls, starts[0]
