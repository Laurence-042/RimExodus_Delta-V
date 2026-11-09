"""Compact shipped sector-content prefabs from one checked layout source.

The records produced here are consumed by build.py for both format-2 prefab XML
and diagrams. Placements use installed ThingDef geometry rather than copied sizes.
"""
from collections import Counter, deque
import json
import math
import re
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont

import combat_range_layout


NAMES = (
    'SectorFighter', 'SectorGunboat', 'SectorFrigate', 'SectorDestroyer', 'SectorCruiser',
    'SectorWeaponRelay', 'SectorAbandonedSatellite', 'SectorWeaponPlatform',
    'SectorTradeStation', 'SectorShipyard', 'SectorFactoryStation', 'SectorAsteroid',
    'AsteroidSteelSmall', 'AsteroidSteelMedium', 'AsteroidComponentsSmall',
    'AsteroidMixedMedium', 'AsteroidPlasteelLarge',
)

ASTEROID_CHILDREN = (
    ('Sector/Asteroids/AsteroidSteelSmall.xml', 4.0),
    ('Sector/Asteroids/AsteroidSteelMedium.xml', 3.0),
    ('Sector/Asteroids/AsteroidComponentsSmall.xml', 1.5),
    ('Sector/Asteroids/AsteroidMixedMedium.xml', 2.0),
    ('Sector/Asteroids/AsteroidPlasteelLarge.xml', 0.75),
)

SPECS = {
    'SectorFighter': dict(size=(17, 19), craft=True, crew=1,
        weapons={'DeltaV_Weapon_RapidMainSmall': 2, 'DeltaV_Weapon_PDTurret': 1}),
    'SectorGunboat': dict(size=(21, 23), craft=True, crew=2,
        weapons={'DeltaV_Weapon_RapidMainSmall': 2, 'DeltaV_Weapon_RapidMainMedium': 1,
                 'DeltaV_Weapon_PDTurret': 2}),
    'SectorFrigate': dict(size=(25, 29), craft=True, crew=4,
        weapons={'DeltaV_Weapon_RapidMainMedium': 2, 'DeltaV_Weapon_LaserMainMedium': 1,
                 'DeltaV_Weapon_PDTurret': 2}, shields=1),
    'SectorDestroyer': dict(size=(29, 35), craft=True, crew=6,
        weapons={'DeltaV_Weapon_RapidMainMedium': 2, 'DeltaV_Weapon_LaserMainMedium': 2,
                 'DeltaV_Weapon_HeavyMainMedium': 1, 'DeltaV_Weapon_PDTurret': 3}, shields=1),
    'SectorCruiser': dict(size=(35, 41), craft=True, crew=10,
        weapons={'DeltaV_Weapon_RapidMainMedium': 3, 'DeltaV_Weapon_LaserMainMedium': 2,
                 'DeltaV_Weapon_HeavyMainMedium': 2, 'DeltaV_Weapon_PDTurret': 4}, shields=2),
    'SectorWeaponRelay': dict(size=(17, 17), crew=1, relay=True),
    'SectorAbandonedSatellite': dict(size=(15, 15), abandoned=True),
    'SectorWeaponPlatform': dict(size=(25, 25), crew=4,
        weapons={'DeltaV_Weapon_RapidMainMedium': 4, 'DeltaV_Weapon_LaserMainMedium': 2,
                 'DeltaV_Weapon_PDTurret': 4}, shields=2),
    'SectorTradeStation': dict(size=(27, 25), crew=4, trade=True),
    'SectorShipyard': dict(size=(31, 29), crew=6, shipyard=True),
    'SectorFactoryStation': dict(size=(29, 27), crew=6, factory=True),
    'SectorAsteroid': dict(size=(19, 19), asteroid=True, clusterCore=True,
        coverage=.34, seed=41,
        minerals=('MineableSteel', 'MineableSteel', 'MineableComponentsIndustrial')),
    'AsteroidSteelSmall': dict(size=(11, 9), asteroid=True, coverage=.24, seed=53,
        minerals=('MineableSteel',)),
    'AsteroidSteelMedium': dict(size=(17, 15), asteroid=True, coverage=.31, seed=67,
        minerals=('MineableSteel', 'MineableSteel', 'MineableComponentsIndustrial')),
    'AsteroidComponentsSmall': dict(size=(13, 11), asteroid=True, coverage=.27, seed=79,
        minerals=('MineableSteel', 'MineableComponentsIndustrial', 'MineableComponentsIndustrial')),
    'AsteroidMixedMedium': dict(size=(19, 17), asteroid=True, coverage=.36, seed=97,
        minerals=('MineableSteel', 'MineableSteel', 'MineableComponentsIndustrial', 'MineablePlasteel')),
    'AsteroidPlasteelLarge': dict(size=(25, 21), asteroid=True, coverage=.22, seed=113,
        minerals=('MineableSteel', 'MineablePlasteel', 'MineablePlasteel')),
}

DIRS = ((1, 0), (-1, 0), (0, 1), (0, -1))


def shape(name):
    spec = SPECS[name]
    w, h = spec['size']
    if spec.get('asteroid'):
        cx, cz = (w - 1) / 2, (h - 1) / 2
        seed = spec['seed']
        cells = {(x, z) for x in range(w) for z in range(h)
                 if ((x-cx)/(w*.48))**2 + ((z-cz)/(h*.46))**2 <= 1
                 and not ((((x*37 + z*61 + seed) % 19) < 3)
                          and ((x-cx)/(w*.48))**2 + ((z-cz)/(h*.46))**2 > .72)}
    else:
        cells = {(x, z) for x in range(w) for z in range(h)}
    return dict(size=(w, h), cells=cells, zones={c: 0 for c in cells},
                foundationDefs={}, surfaceDefs={}, roofDefs={})


def furnish(name, plan, place, prop, foundation, roofs, records, occupied):
    spec = SPECS[name]
    w, h = spec['size']
    reserved = set()

    def adj(c):
        return [(c[0]+dx, c[1]+dz) for dx, dz in DIRS]

    def put(thing, x, z, rot=0, **kw):
        record = place(thing, x, z, rot, **kw)
        if record['hasInteraction']:
            ix, _, iz = record['interaction']
            dx, dz = [(ix, iz), (iz, -ix), (-ix, -iz), (-iz, ix)][rot]
            reserved.add((record['root'][0]+dx, record['root'][1]+dz))
        return record

    def dims(thing, rot):
        size = list(map(int, re.findall(r'-?\d+', prop(thing, 'size', '(1,1)'))))
        return size if rot % 2 == 0 else size[::-1]

    def auto(thing, area, rotations=(0, 1, 2, 3), state=None, near=None):
        area = set(area)
        near = near or (w/2, h/2)
        candidates = sorted(area, key=lambda c: ((c[0]-near[0])**2+(c[1]-near[1])**2, c))
        offset = list(map(int, re.findall(r'-?\d+', prop(thing, 'interactionCellOffset', '(0,0,0)'))))
        has = prop(thing, 'hasInteractionCell', 'false').lower() == 'true'
        for x, z in candidates:
            for rot in rotations:
                dw, dh = dims(thing, rot)
                cells = {(x+a, z+b) for a in range(dw) for b in range(dh)}
                if not cells <= area or cells & occupied.keys() or cells & reserved:
                    continue
                rx = x+(dw-1)//2+(1 if dw % 2 == 0 and rot in (2, 3) else 0)
                rz = z+(dh-1)//2+(1 if dh % 2 == 0 and rot in (1, 2) else 0)
                ix, _, iz = offset
                dx, dz = [(ix, iz), (iz, -ix), (-ix, -iz), (-iz, ix)][rot]
                interaction = (rx+dx, rz+dz)
                if has and (interaction not in area or interaction in occupied
                            or (interaction in cells and prop(thing, 'passability', 'Standable') == 'Impassable')):
                    continue
                if not any(n in area and n not in occupied and n not in cells for c in cells for n in adj(c)):
                    continue
                return put(thing, x, z, rot, state=state)
        raise ValueError(f'No placement for {thing} in {name}')

    if spec.get('asteroid'):
        # Match Odyssey GenStep_Asteroid: the pocket map outside the authored
        # footprint remains Space, while the asteroid surface is Vacstone's
        # implied natural terrain rather than a terrestrial stone terrain.
        plan['surfaceDefs'] = {c: 'Vacstone_Rough' for c in foundation}
        plan['omitFoundations'] = True
        plan['omitEnvironment'] = True
        roofs.clear()
        center = min(foundation, key=lambda c: math.dist(c, (w/2, h/2)))
        if spec.get('clusterCore'):
            marker = put('DeltaV_PrefabClusterMarker', *center)
            marker['stateOverrides'].update({
                '_clusterEntries': ASTEROID_CHILDREN,
                '_contentTypeId': 'Asteroid',
                '_innerRadius': 70,
                '_outerRadius': 260,
                '_densityPerTenThousand': .6,
                '_minimumSpacing': 28,
                '_inheritFaction': False,
                '_seed': 40771,
            })
        # The native generator uses a lower elevation threshold for rough
        # surface and a higher one for solid Vacstone plus thin rock roof.
        # A one-cell erosion gives the authored prefabs the same two-layer
        # structure while preserving their deterministic jagged silhouettes.
        rock_cells = {c for c in foundation
                      if all((c[0]+dx, c[1]+dz) in foundation for dx, dz in DIRS)}
        rock_cells.difference_update(occupied)
        roofs.update(rock_cells)
        plan['roofDefs'] = {c: 'RoofRockThin' for c in rock_cells}
        candidates = list(rock_cells)
        candidates.sort(key=lambda c: (((c[0]*73856093) ^ (c[1]*19349663)
                                        ^ spec['seed']) & 0x7fffffff, c))
        mineral_count = min(len(candidates) - 1,
                            max(1, round(len(foundation) * spec['coverage'])))
        minerals = spec['minerals']
        mineral_defs = {cell: minerals[index % len(minerals)]
                        for index, cell in enumerate(candidates[:mineral_count])}
        for x, z in sorted(rock_cells):
            put(mineral_defs.get((x, z), 'Vacstone'), x, z)
        return set(), center

    engines = []
    if spec.get('craft'):
        for x in (3, w-6):
            engines.extend((put('LargeThruster', x, 0), put('LargeThruster', x, h-2, 2)))
        for z in (4, h-6):
            engines.extend((put('SmallThruster', 0, z, 1), put('SmallThruster', w-2, z, 3)))
    engine_cells = {tuple(c) for r in engines for c in r['cells']}
    roofs.difference_update(engine_cells)
    exterior = {c for c in foundation if any(n not in foundation for n in adj(c))}
    ports = {(w-1, h//2): 1}
    walls = exterior - engine_cells - set(ports)
    for x, z in sorted(walls):
        put('GravshipHull', x, z)
    for (x, z), rot in ports.items():
        put('DeltaV_DockingPort', x, z, rot)
    room = foundation - walls - engine_cells - set(ports)
    start = (w-2, h//2)

    # Keep a cross-shaped circulation spine clear while machinery is packed around it.
    reserved.update({c for c in room if c[0] == w//2 or c[1] == h//2})
    reserved.discard(start)

    auto('PilotConsole', room, near=(w/2, h-4))
    if spec.get('craft'):
        auto('GravEngine', room, near=(w/2, 5))
    if spec.get('relay'):
        auto('CommsConsole', room)
        auto('HiTechResearchBench', room)
    if spec.get('abandoned'):
        auto('CommsConsole', room)
        auto('ShelfSmall', room)
        auto('Steel', room, rotations=(0,), state={'stackCount': 40})
    if spec.get('trade'):
        supply = [auto('DeltaV_CargoContainer', room, rotations=(0,)) for _ in range(3)]
        delivery = auto('DeltaV_CargoStorage', room, rotations=(0,),
                        state={'allowedDefs': ['Steel', 'Plasteel', 'ComponentIndustrial',
                                               'ComponentSpacer', 'Chemfuel', 'Silver',
                                               'MealSurvivalPack', 'MedicineIndustrial']})
        terminal = auto('DeltaV_TradeTerminal', room)
        terminal['stateOverrides']['_supplyCargo'] = supply
        terminal['stateOverrides']['_deliveryCargo'] = delivery
        auto('OrbitalTradeBeacon', room)
        auto('CommsConsole', room)
    if spec.get('shipyard'):
        for thing in ('TableMachining', 'FabricationBench', 'ElectricSmithy',
                      'DeltaV_EvaSuit', 'DeltaV_EvaSuit'):
            auto(thing, room)
        for _ in range(4):
            auto('DeltaV_CargoStorage', room, rotations=(0,),
                 state={'allowedDefs': ['Steel', 'Plasteel', 'ComponentIndustrial', 'ComponentSpacer']})
        auto('CommsConsole', room)
    if spec.get('factory'):
        for thing in ('TableMachining', 'FabricationBench', 'ElectricSmithy',
                      'ElectricTailoringBench', 'HiTechResearchBench'):
            auto(thing, room)
        for _ in range(6):
            auto('DeltaV_CargoStorage', room, rotations=(0,),
                 state={'allowedDefs': ['Steel', 'Plasteel', 'ComponentIndustrial',
                                        'ComponentSpacer', 'Chemfuel']})
        auto('CommsConsole', room)

    for weapon, count in spec.get('weapons', {}).items():
        for index in range(count):
            auto(weapon, room, near=(2 if index % 2 == 0 else w-3,
                                     3 + (index//2)*5))
    for index in range(spec.get('shields', 0)):
        auto('DeltaV_ShieldGenerator', room, near=(w/2, 4+index*7),
             state={'DeltaV_shieldProjection': '28', 'DeltaV_shieldLength': '28'})

    for _ in range(spec.get('crew', 0)):
        auto('DeltaV_PrefabPawnMarker', room, rotations=(0,))
    if spec.get('crew', 0):
        for _ in range(max(1, spec['crew']//2)):
            auto('Bed', room)

    # Life support is mounted against a real hull cell.
    pump_count = 2 if w >= 25 else 1
    for _ in range(pump_count):
        placed = False
        for x, z in sorted(room):
            if (x, z) in occupied or (x, z) in reserved:
                continue
            for rot, (dx, dz) in enumerate(((0, 1), (1, 0), (0, -1), (-1, 0))):
                if (x+dx, z+dz) in walls:
                    put('OxygenPump', x, z, rot)
                    placed = True
                    break
            if placed:
                break
        if not placed:
            raise ValueError(f'No oxygen mounting cell in {name}')
    auto('Heater', room)
    auto('StandingLamp', room)

    # Size the always-on source against actual Def-derived nameplate load.
    demand = sum(max(0, r['powerWatts']) for r in records)
    source_watts = 0
    while source_watts < demand + 1200:
        source = auto('GravcorePowerCell', room)
        source_watts += -min(0, source['powerWatts'])
    for _ in range(max(1, math.ceil(demand / 6000))):
        auto('Battery', room)

    transmitting = {tuple(c) for r in records if r['transmitsPower'] for c in r['cells']}
    for x, z in sorted(foundation - transmitting):
        put('HiddenConduit', x, z, layer='conduit')
    plan['walls'] = walls
    plan['ports'] = ports
    return walls, start


def prepare_state(record, state, field, prop):
    combat_range_layout.prepare_state(record, state, field, prop)
    name = record['name']
    if record['category'] == 'Building':
        hp = float(prop(name, 'statBases/MaxHitPoints', '100'))
        if record['stuff']:
            hp = hp * float(prop(record['stuff'], 'stuffProps/statFactors/MaxHitPoints', '1'))
            hp += float(prop(record['stuff'], 'stuffProps/statOffsets/MaxHitPoints', '0'))
        field(state, 'health', round(hp))
    if name == 'DeltaV_TradeTerminal':
        records = record['stateOverrides']
        supply = state.find('supplyCargo')
        if supply is None:
            supply = ET.SubElement(state, 'supplyCargo')
        for child in list(supply):
            supply.remove(child)
        for cargo in records['_supplyCargo']:
            ET.SubElement(supply, 'li').text = cargo['_loadId']
        field(state, 'deliveryCargo', records['_deliveryCargo']['_loadId'])
        field(state, 'traderKind', 'Orbital_BulkGoods')
        field(state, 'restockDays', '15')
        field(state, 'lastRestockTick', '-1')
        field(state, 'tradeStockGenerated', 'False')
        field(state, 'indiscriminateSupply', 'True')
        field(state, 'tradePriceSeed', '7319')
    if name == 'DeltaV_PrefabClusterMarker':
        overrides = record['stateOverrides']
        prefabs = state.find('prefabs')
        if prefabs is None:
            prefabs = ET.SubElement(state, 'prefabs')
        for child in list(prefabs):
            prefabs.remove(child)
        for path, weight in overrides['_clusterEntries']:
            entry = ET.SubElement(prefabs, 'li', {'Class': 'DeltaV.PrefabClusterEntry'})
            field(entry, 'prefabPath', path)
            field(entry, 'weight', weight)
        field(state, 'contentTypeId', overrides['_contentTypeId'])
        field(state, 'innerRadius', overrides['_innerRadius'])
        field(state, 'outerRadius', overrides['_outerRadius'])
        field(state, 'densityPerTenThousand', overrides['_densityPerTenThousand'])
        field(state, 'minimumSpacing', overrides['_minimumSpacing'])
        field(state, 'inheritFaction', overrides['_inheritFaction'])
        field(state, 'seed', overrides['_seed'])


def validate_and_draw(name, plan, records, foundation, roofs, reachable, here, generated):
    counts = Counter(r['name'] for r in records)
    spec = SPECS[name]
    if spec.get('asteroid'):
        mineables = sum(count for thing, count in counts.items() if thing.startswith('Mineable'))
        assert mineables > 0 and counts['Vacstone'] > 0, (name, 'partial mineral coverage')
        assert mineables + counts['Vacstone'] == len(roofs), (name, 'solid asteroid body')
        assert set(plan['surfaceDefs'].values()) == {'Vacstone_Rough'}
        assert set(plan['roofDefs'].values()) == {'RoofRockThin'}
        assert plan['omitFoundations']
        assert plan['omitEnvironment']
        assert counts['DeltaV_PrefabClusterMarker'] == (1 if spec.get('clusterCore') else 0)
    else:
        assert counts['PilotConsole'] == 1
        assert counts['DeltaV_DockingPort'] == 1
        assert counts['OxygenPump'] >= 1
        assert counts['GravcorePowerCell'] >= 1
        assert counts['DeltaV_PrefabClusterMarker'] == 0
        assert all(tuple(r['root']) in reachable for r in records
                   if r['name'] == 'DeltaV_PrefabPawnMarker')
        generation = -sum(min(0, r['powerWatts']) for r in records)
        load = sum(max(0, r['powerWatts']) for r in records)
        assert generation >= load + 1200, (name, 'power deficit', generation, load)
    # Trade references are checked against the exact identities build.py will serialize.
    if spec.get('trade'):
        terminal = next(r for r in records if r['name'] == 'DeltaV_TradeTerminal')
        assert len(terminal['stateOverrides']['_supplyCargo']) == 3
        assert terminal['stateOverrides']['_deliveryCargo']['name'] == 'DeltaV_CargoStorage'

    report = {'name': name, 'size': list(spec['size']), 'counts': dict(sorted(counts.items())),
              'foundationCells': 0 if plan.get('omitFoundations') else len(foundation),
              'naturalTerrainCells': len(foundation), 'roofCells': len(roofs),
              'stage': 'offline authored content; game SpawnSetup pending'}
    (generated / (name + '.layout.json')).write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

    w, h = spec['size']
    scale = max(9, min(22, 520 // max(w, h)))
    pad = 45
    listed = min(15, len(counts))
    image_height = max(h*scale + 2*pad, pad + 48 + listed*24 + 50)
    image = Image.new('RGB', (w*scale + 2*pad + 390, image_height), '#101822')
    draw = ImageDraw.Draw(image)
    for x, z in foundation:
        draw.rectangle((pad+x*scale, pad+(h-z-1)*scale,
                        pad+(x+1)*scale, pad+(h-z)*scale),
                       fill='#544f49' if spec.get('asteroid') else '#344a58', outline='#435967')
    colors = {'GravshipHull': '#8799a2', 'DeltaV_DockingPort': '#f0c96e',
              'PilotConsole': '#ef9f55', 'GravEngine': '#d9b569',
              'DeltaV_PrefabPawnMarker': '#f1dce8', 'OxygenPump': '#71c6d0',
              'DeltaV_CargoContainer': '#c49058', 'DeltaV_CargoStorage': '#e0b36c',
              'DeltaV_TradeTerminal': '#6fc99c', 'MineableSteel': '#777f88',
              'MineableComponentsIndustrial': '#b48d62', 'MineablePlasteel': '#819ab4',
              'Vacstone': '#5f5750',
              'DeltaV_PrefabClusterMarker': '#ffb833'}
    for record in records:
        if record['layer'] in ('conduit', 'items'):
            continue
        xs, zs = zip(*record['cells'])
        box = (pad+min(xs)*scale+1, pad+(h-max(zs)-1)*scale+1,
               pad+(max(xs)+1)*scale-1, pad+(h-min(zs))*scale-1)
        color = colors.get(record['name'], '#b6b9b5')
        if record['name'].startswith('DeltaV_Weapon_'):
            color = '#d58555'
        elif 'Shield' in record['name']:
            color = '#6e9ed3'
        draw.rectangle(box, fill=color)
    font_path = 'C:/Windows/Fonts/msyh.ttc'
    font = lambda size: ImageFont.truetype(font_path, size)
    x = w*scale + 2*pad
    draw.text((x, pad), name, font=font(26), fill='#eef3f5')
    y = pad + 48
    for key, value in sorted(counts.items(), key=lambda p: (-p[1], p[0]))[:15]:
        draw.text((x, y), f'{key}: {value}', font=font(16), fill='#c5d1d7')
        y += 24
    draw.text((x, image_height-28), 'North up · prefab/diagram same records',
              font=font(15), fill='#95aebc')
    image.save(generated / (name + '.png'))
