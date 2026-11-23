"""Compact shipped sector-content prefabs from one checked layout source.

The records produced here are consumed by build.py for both format-2 prefab XML
and diagrams. Placements use installed ThingDef geometry rather than copied sizes.
"""
from collections import Counter, deque
import json
import math
from pathlib import Path
import re
import tempfile
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont
from station_layout import prepare_trade_state

import combat_range_layout
import sector_craft
import sector_settlements


NAMES = (
    'SectorFighter', 'SectorGunboat', 'SectorFrigate', 'SectorDestroyer', 'SectorCruiser',
    'SectorWeaponRelay', 'SectorAbandonedSatellite', 'SectorWeaponPlatform',
    'SectorTradeStation', 'SectorShipyard', 'SectorFactoryStation',
    'SectorAsteroid',
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
    'SectorWeaponRelay': dict(size=(17, 17), crew=1, relay=True),
    'SectorAbandonedSatellite': dict(size=(15, 15), abandoned=True),
    'SectorWeaponPlatform': dict(size=(25, 25), crew=4,
        weapons={'DeltaV_Weapon_RapidMainMedium': 4, 'DeltaV_Weapon_LaserMainMedium': 2,
                 'DeltaV_Weapon_PDTurret': 4}, shields=2),
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
FACING = ((0, 1), (1, 0), (0, -1), (-1, 0))
ROTATIONS = ('North', 'East', 'South', 'West')

# Inclusive bands (south, north, west, east). Multiple bands at the same
# latitude leave real vacuum between the pods, prongs or dock arms.
HULLS = {
    'SectorWeaponRelay': dict(
        bands=((0, 16, 6, 11), (4, 12, 2, 14), (7, 10, 0, 16)),
        solar=(1, 17, 15, 26), solarDoor=(8, 16, 0),
        port=(8, 0, 2), bridge=(8, 4), machinery=(8, 13), quarters=(5, 7),
        work=((12, 9), (7, 11))),
    'SectorAbandonedSatellite': dict(
        bands=((0, 12, 2, 9), (3, 8, 0, 12), (6, 9, 13, 14), (9, 14, 5, 11)),
        solar=(1, 15, 15, 24), solarDoor=(8, 14, 0),
        port=(6, 0, 2), bridge=(7, 10), machinery=(4, 3), quarters=(6, 5),
        work=((9, 7), (4, 6), (4, 7))),
    'SectorWeaponPlatform': dict(
        bands=((0, 4, 7, 17), (5, 10, 0, 24), (11, 13, 4, 20),
               (14, 19, 0, 24), (20, 24, 7, 17)),
        port=(12, 0, 2), bridge=(11, 11), machinery=(9, 3), quarters=(13, 21),
        solar=(0, 25, 24, 34), solarDoor=(12, 24, 0),
        shields=((8, 2), (14, 20)),
        weapons=((2, 7), (19, 7), (2, 15), (19, 15), (8, 19), (14, 19),
                 (1, 9), (23, 9), (1, 17), (23, 17))),

}


def shape(name, footprint):
    if name in sector_craft.CRAFT:
        return sector_craft.shape(name, footprint)
    if name in sector_settlements.STATIONS:
        return sector_settlements.shape(name)
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
        cells = {(x, z) for south, north, west, east in HULLS[name]['bands']
                 for x in range(west, east + 1) for z in range(south, north + 1)}
    solar = sector_settlements.rectangle(HULLS[name]['solar']) if 'solar' in HULLS.get(name, {}) else set()
    cells |= solar
    return dict(size=(max(w, max((x+1 for x, _ in solar), default=0)),
                      max(h, max((z+1 for _, z in solar), default=0))),
                cells=cells, zones={c: 0 for c in cells}, noRoofCells=solar,
                foundationDefs={}, surfaceDefs={}, roofDefs={})


def furnish(name, plan, place, footprint, prop, foundation, roofs, records, occupied):
    spec = SPECS.get(name, {})
    w, h = plan['size']
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

    def auto(thing, area, rotations=(0, 1, 2, 3), state=None, near=None, mirror_axis=None, center_axis=None):
        area = set(area)
        near = near or (w/2, h/2)
        candidates = sorted(area, key=lambda c: ((c[0]-near[0])**2+(c[1]-near[1])**2, c))
        offset = list(map(int, re.findall(r'-?\d+', prop(thing, 'interactionCellOffset', '(0,0,0)'))))
        has = prop(thing, 'hasInteractionCell', 'false').lower() == 'true'
        rotatable = prop(thing, 'rotatable', 'true').lower() == 'true'
        if not rotatable:
            rotations = (ROTATIONS.index(prop(thing, 'defaultPlacingRot', 'North')),)
        def fit(rot, root, cells):
            if not cells <= area or cells & occupied.keys() or cells & reserved:
                return False, None
            ix, _, iz = offset
            dx, dz = [(ix, iz), (iz, -ix), (-ix, -iz), (-iz, ix)][rot]
            interaction = (root[0]+dx, root[1]+dz) if has else None
            if has and (interaction not in area or interaction in occupied
                        or (interaction in cells and prop(thing, 'passability', 'Standable') == 'Impassable')):
                return False, interaction
            return any(n in area and n not in occupied and n not in cells for c in cells for n in adj(c)), interaction
        for x, z in candidates:
            for rot in rotations:
                _, root, cells = footprint(thing, x, z, rot)
                east = max(a for a, _ in cells)
                if center_axis is not None and x+east != 2*center_axis:
                    continue
                fits, interaction = fit(rot, root, cells)
                if not fits:
                    continue
                if mirror_axis is None:
                    return put(thing, x, z, rot, state=state)
                rx, rrot = 2*mirror_axis-east, (-rot % 4 if rotatable else rot)
                if rx <= east:
                    continue
                _, right_root, right_cells = footprint(thing, rx, z, rrot)
                right_fits, right_interaction = fit(rrot, right_root, right_cells)
                if not right_fits or interaction in right_cells or right_interaction in cells:
                    continue
                pair_id = len(records)
                pair = (put(thing, x, z, rot, state=state), put(thing, rx, z, rrot, state=state))
                for record in pair:
                    record['mirrorPair'] = pair_id
                return pair
        raise ValueError(f'No placement for {thing} in {name}')

    def life_support(area, walls, anchor, label='cabin'):
        # Space's -75 C and a constructed roof lose about 1.2 C per 250 ticks
        # at 21 C. A 21 energy/s heater can cover about 72 cells at that load.
        def spread(cell, existing):
            return (-min((math.dist(cell, c) for c in existing), default=0), math.dist(cell, anchor), cell)

        heaters = []
        for _ in range(math.ceil(len(area) / 60)):
            near = min(area - occupied.keys() - reserved, key=lambda c: spread(c, heaters))
            heater = auto('Heater', area, near=near, state={'targetTemperature': 21})
            heaters.append(heater['root'])
        pumps = []
        for _ in range(math.ceil(len(area) / 180)):
            mounts = [(x, z, rot) for x, z in area - occupied.keys() - reserved
                      for rot, (dx, dz) in enumerate(FACING) if (x+dx, z+dz) in walls]
            if not mounts:
                raise ValueError(f'No oxygen mounting cell in {name} near {anchor}')
            pump = put('OxygenPump', *min(mounts, key=lambda c: (spread(c[:2], pumps), c[2])))
            pumps.append(pump['root'])
        plan.setdefault('cabins', []).append(dict(name=label, cells=sorted(area), temperature=21))

    def finish_structure(roof_cells, sparse_grid=False):
        # Roof supports may be no farther than 6.9 cells from the roof tile.
        offsets = [(dx, dz) for dx in range(-6, 7) for dz in range(-6, 7)
                   if dx*dx+dz*dz <= 6.9**2]
        def cover(cell):
            return {(cell[0]+dx, cell[1]+dz) for dx, dz in offsets}
        holders = {tuple(c) for r in records if r['holdsRoof'] for c in r['cells']}
        unsupported = roof_cells - set().union(*(cover(c) for c in holders))
        while unsupported:
            target = min(unsupported)
            candidates = cover(target) & (foundation - occupied.keys() - reserved)
            post = min(candidates, key=lambda c: (-len(cover(c) & unsupported), c))
            put('GravshipHull', *post)
            unsupported.difference_update(cover(post))

        transmitting = {tuple(c) for r in records if r['transmitsPower'] for c in r['cells']}
        grid = foundation
        if sparse_grid:
            # Share cable runs from one service tree. Each powered footprint
            # gets a transmitter directly underneath, or conducts itself.
            root = min(transmitting)
            parent = {root: None}
            queue = deque([root])
            while queue:
                cell = queue.popleft()
                for neighbor in adj(cell):
                    if neighbor in foundation and neighbor not in parent:
                        parent[neighbor] = cell
                        queue.append(neighbor)
            assert parent.keys() == foundation, (name, 'disconnected foundation')
            targets = transmitting | {min(map(tuple, r['cells'])) for r in records if r['powerWatts'] > 0}
            grid = {root}
            for cell in sorted(targets):
                while cell not in grid:
                    grid.add(cell)
                    cell = parent[cell]
        for x, z in sorted(grid - transmitting):
            put('HiddenConduit', x, z, layer='conduit')

    if spec.get('asteroid'):
        plan['surfaceDefs'] = {c: 'Space' for c in foundation}
        plan['omitFoundations'] = True
        plan['omitEnvironment'] = True
        roofs.clear()
        center = min(foundation, key=lambda c: math.dist(c, (w/2, h/2)))
        if spec.get('clusterCore'):
            marker = put('DeltaV_PrefabClusterMarker', *center)
            marker['stateOverrides'].update({
                '_clusterEntries': ASTEROID_CHILDREN,
                '_innerRadius': 70,
                '_outerRadius': 260,
                '_densityPerTenThousand': .6,
                '_minimumSpacing': 28,
                '_inheritFaction': False,
                '_seed': 40771,
            })
        # Retain the original rock footprint and ore distribution.
        rock_cells = {c for c in foundation
                      if all((c[0]+dx, c[1]+dz) in foundation for dx, dz in DIRS)}
        rock_cells.difference_update(occupied)
        candidates = list(rock_cells)
        candidates.sort(key=lambda c: (((c[0]*73856093) ^ (c[1]*19349663)
                                        ^ spec['seed']) & 0x7fffffff, c))
        mineral_count = min(len(candidates) - 1,
                            max(1, round(len(foundation) * spec['coverage'])))
        minerals = spec['minerals']
        mineral_defs = {cell: minerals[index % len(minerals)]
                        for index, cell in enumerate(candidates[:mineral_count])}
        for x, z in sorted(rock_cells):
            rock = put(mineral_defs.get((x, z), 'Vacstone'), x, z)
            rock['stateOverrides']['canChangeTerrainOnDestroyed'] = 'false'
        return set(), center

    if name in sector_craft.CRAFT:
        return sector_craft.furnish(name, plan, foundation, roofs, records, occupied, reserved,
                                    put, auto, footprint, prop, life_support, finish_structure)
    if name in sector_settlements.STATIONS:
        return sector_settlements.furnish(name, plan, foundation, roofs, records, occupied, reserved,
                                          put, auto, prop, life_support, finish_structure)

    hull = HULLS[name]
    solar = plan['noRoofCells']
    roofs.difference_update(solar)
    hull_cells = foundation - solar
    exterior = {c for c in hull_cells if any(n not in hull_cells for n in adj(c))}
    px, pz, facing = hull['port']
    port = (px, pz)
    dx, dz = FACING[facing]
    start = (px-dx, pz-dz)
    assert port in exterior and (px+dx, pz+dz) not in foundation, (name, 'blocked port')
    ports = {port: facing}
    walls = exterior - set(ports)
    service_inside = None
    if solar:
        sx, sz, rot = hull['solarDoor']
        dx, dz = FACING[rot]
        service_inside, service_outside = (sx-dx, sz-dz), (sx+dx, sz+dz)
        assert (sx, sz) in walls and service_outside in solar and service_inside in hull_cells - walls
        walls.remove((sx, sz))
        put('Door', sx, sz, rot)
        reserved.update((service_inside, service_outside))
    bulkheads = set(hull.get('bulkheads', ()))
    assert bulkheads <= foundation - exterior
    for x, z in sorted(walls):
        put('GravshipHull', x, z)
    for x, z in sorted(bulkheads):
        put('GravshipHull', x, z)
    for (x, z), rot in ports.items():
        put('DeltaV_DockingPort', x, z, rot)
    room = hull_cells - walls - bulkheads - set(ports)
    if solar:
        room.remove((sx, sz))
    assert start in room, (name, 'blocked internal port approach', start)
    # Connect the entrance to each functional section before packing equipment.
    # Paths follow the authored hull, including around the drydock and split pods.
    paths = {start: None}
    queue = deque([start])
    while queue:
        parent = queue.popleft()
        for cell in adj(parent):
            if cell in room and cell not in paths:
                paths[cell] = parent
                queue.append(cell)
    assert paths.keys() == room, (name, 'disconnected interior', sorted(room - paths.keys()))
    anchors = (hull['bridge'], hull['machinery'], hull['quarters'], *hull.get('work', ()))
    for anchor in (*anchors, service_inside) if solar else anchors:
        cell = min(paths, key=lambda c: (math.dist(c, anchor), c))
        while cell is not None:
            reserved.add(cell)
            cell = paths[cell]
    circulation = set(reserved)

    auto('PilotConsole', room, rotations=(0,), near=hull['bridge'])
    work = iter(hull.get('work', ()))
    if spec.get('relay'):
        auto('CommsConsole', room, near=next(work))
        auto('HiTechResearchBench', room, near=next(work))
    if spec.get('abandoned'):
        auto('CommsConsole', room, near=next(work))
        auto('ShelfSmall', room, near=next(work))
        auto('Steel', room, rotations=(0,), state={'stackCount': 40}, near=next(work))
    for anchor in hull.get('shields', tuple((w//2, 4+index*7)
                                          for index in range(spec.get('shields', 0)))):
        auto('DeltaV_ShieldGenerator', room, near=anchor,
             state={'DeltaV_shieldProjection': '28', 'DeltaV_shieldLength': '28'})
    emplacements = iter(hull.get('weapons', ()))
    for weapon, count in spec.get('weapons', {}).items():
        for _ in range(count):
            auto(weapon, room, near=next(emplacements))
    for _ in range(spec.get('crew', 0)):
        auto('DeltaV_PrefabPawnMarker', room, rotations=(0,), near=hull['quarters'])
    if spec.get('crew', 0):
        for _ in range(max(1, spec['crew']//2)):
            auto('Bed', room, near=hull['quarters'])

    life_support(room, walls, hull['quarters'])
    auto('StandingLamp', room, near=hull['bridge'])
    sector_settlements.install_solar(name, plan, foundation, records, reserved, put, auto, prop,
                                     room, (sx, sz), service_outside)
    finish_structure(roofs, sparse_grid=True)

    deck_colors = {'Structure_BlueSubtle': (hull['bridge'],),
                   'Structure_BrownSubtle': (hull['machinery'],),
                   'Structure_Teal': (hull['quarters'],),
                   'Structure_Mustard': hull.get('work', ()),
                   'Structure_RedSubtle': hull.get('weapons', ())}
    color_points = [(color, anchor) for color, anchors in deck_colors.items() for anchor in anchors]
    painted = {color: set() for color in deck_colors}
    for cell in room - circulation:
        color, anchor = min(color_points, key=lambda entry: math.dist(cell, entry[1]))
        if math.dist(cell, anchor) <= 4:
            painted[color].add(cell)
    plan['rooms'] = [dict(color=color, cells=cells) for color, cells in painted.items() if cells]

    plan['walls'] = walls
    plan['ports'] = ports
    return walls, start


def prepare_state(record, state, field, prop):
    combat_range_layout.prepare_state(record, state, field, prop)
    name = record['name']
    if name == 'PilotConsole':
        field(state.find('combat'), 'fireControlMode', 'Automatic')
    if record['category'] == 'Building':
        hp = float(prop(name, 'statBases/MaxHitPoints', '100'))
        if record['stuff']:
            hp = hp * float(prop(record['stuff'], 'stuffProps/statFactors/MaxHitPoints', '1'))
            hp += float(prop(record['stuff'], 'stuffProps/statOffsets/MaxHitPoints', '0'))
        field(state, 'health', round(hp))
    if name == 'DeltaV_TradeTerminal':
        prepare_trade_state(record, state, field, indiscriminate_supply=True)
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
        field(state, 'innerRadius', overrides['_innerRadius'])
        field(state, 'outerRadius', overrides['_outerRadius'])
        field(state, 'densityPerTenThousand', overrides['_densityPerTenThousand'])
        field(state, 'minimumSpacing', overrides['_minimumSpacing'])
        field(state, 'inheritFaction', overrides['_inheritFaction'])
        field(state, 'seed', overrides['_seed'])


def validate_and_draw(name, plan, records, foundation, roofs, reachable, here, generated):
    counts = Counter(r['name'] for r in records)
    spec = SPECS.get(name, {})
    w, h = plan['size']
    generation = -sum(min(0, r['powerWatts']) for r in records
                      if r['stateOverrides'].get('switchOn') != 'False')
    load = sum(max(0, r['powerWatts']) for r in records)
    if spec.get('asteroid'):
        mineables = sum(count for thing, count in counts.items() if thing.startswith('Mineable'))
        assert mineables > 0 and counts['Vacstone'] > 0, (name, 'partial mineral coverage')
        assert not roofs and not plan['roofDefs']
        assert set(plan['surfaceDefs'].values()) == {'Space'}
        assert plan['omitFoundations']
        assert plan['omitEnvironment']
        assert counts['DeltaV_PrefabClusterMarker'] == (1 if spec.get('clusterCore') else 0)
    else:
        assert len(foundation) < w * h
        assert counts['PilotConsole'] == (2 if name == 'SectorCruiser' else 1)
        assert counts['DeltaV_DockingPort'] == (2 if name == 'SectorTradeStation' or name in sector_craft.CRAFT else 1)
        assert counts['OxygenPump'] >= 1
        assert counts['GravcorePowerCell'] == 0
        assert counts['DeltaV_PrefabClusterMarker'] == 0
        barrier = {tuple(c) for r in records if r['airtight'] for c in r['cells']}
        for cabin in plan['cabins']:
            cells = set(map(tuple, cabin['cells']))
            assert cells <= roofs and all((x+dx, z+dz) in cells or (x+dx, z+dz) in barrier
                                          for x, z in cells for dx, dz in DIRS), (name, cabin['name'], 'unsealed')
        assert all(tuple(r['root']) in reachable for r in records
                   if r['name'] == 'DeltaV_PrefabPawnMarker')
        for r in records:
            if r['layer'] != 'body' or r['name'] in ('GravshipHull', 'DeltaV_DockingPort'):
                continue
            footprint = set(map(tuple, r['cells']))
            assert any((x+dx, z+dz) not in footprint and (x+dx, z+dz) in reachable
                       for x, z in footprint for dx, dz in DIRS), (name, r['name'], r['root'], 'no service approach')
        assert generation >= load + (0 if name == 'SectorFighter' else 1200), (
            name, 'power deficit', generation, load)
        if plan.get('solarBudget'):
            budget = plan['solarBudget']
            assert counts['SolarGenerator'] == budget['panels']
            assert counts['Battery'] == budget['batteries']
            assert budget['capacityWd'] > budget['deficitWd']
            assert budget['rechargeWd'] > budget['deficitWd']
            assert all(set(map(tuple, r['cells'])) <= plan['noRoofCells']
                       for r in records if r['name'] == 'SolarGenerator')
    if name in sector_settlements.STATIONS:
        crew = sector_settlements.STATIONS[name]['crew']
        basins = 2 * sector_settlements.BASINS_PER_FARM
        assert counts['Bed'] == counts['DeltaV_PrefabPawnMarker'] == crew
        assert counts['HydroponicsBasin'] == basins and counts['Plant_Rice'] == basins * 4
        assert counts['ElectricStove'] == 2 and counts['DiningChair'] >= crew
        assert counts['ChessTable'] == counts['HoopstoneRing'] == 1
    # Trade references are checked against the exact identities build.py will serialize.
    if name == 'SectorTradeStation':
        terminal = next(r for r in records if r['name'] == 'DeltaV_TradeTerminal')
        assert len(terminal['stateOverrides']['_supplyCargo']) == 3
        assert terminal['stateOverrides']['_deliveryCargo']['name'] == 'DeltaV_CargoStorage'

    report = {'name': name, 'size': list(plan['size']), 'counts': dict(sorted(counts.items())),
              'foundationCells': 0 if plan.get('omitFoundations') else len(foundation),
              'naturalTerrainCells': 0 if spec.get('asteroid') else len(foundation),
              'roofCells': len(roofs), 'roofs': sorted(roofs),
              'cabins': plan.get('cabins', []), 'foundation': sorted(foundation),
              'generationW': generation, 'nameplateLoadW': load,
              'solarBudget': plan.get('solarBudget'),
              'stage': 'offline authored content; game SpawnSetup pending'}
    if 'mirrorAxis' in plan:
        report['mirrorAxis'] = plan['mirrorAxis']
        report['symmetricShell'] = plan['symmetricShell']
        report['moduleFloorCells'] = {key: len(cells) for key, cells in plan['modules'].items()}
        report['armorCells'] = sorted(plan['armorCells'])
    (generated / (name + '.layout.json')).write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

    scale = max(9, min(22, 520 // max(w, h)))
    pad = 45
    modules = plan.get('modules', {})
    listed = min(15, len(counts))
    aisles = plan.get('aisles', set())
    armor = plan.get('armorCells', set())
    guards = [r['stateOverrides']['DeltaV_shieldGroundRadius'] for r in records
              if r['stateOverrides'].get('DeltaV_shieldMode') == 'Guard']
    propulsion_legend = (108 if aisles else 84) if 'mirrorAxis' in plan else 0
    protection_legend = 24 * (bool(armor) + bool(guards))
    image_height = max(h*scale + 2*pad, pad + 48 + (listed+len(modules))*24 + 50 + propulsion_legend + protection_legend)
    image = Image.new('RGB', (w*scale + 2*pad + 390, image_height), '#101822')
    draw = ImageDraw.Draw(image)
    zone_palette = {'Structure_BlueSubtle': '#435165', 'Structure_BrownSubtle': '#66564a',
                    'Structure_Teal': '#32696a', 'Structure_Mustard': '#70613d',
                    'Structure_RedSubtle': '#6e474a', 'Structure_GreenPine': '#395849',
                    'Structure_PurpleSubtle': '#554961', 'Structure_BlueIce': '#526988'}
    floor_colors = {cell: zone_palette[zone['color']]
                    for zone in plan.get('rooms', ()) for cell in zone['cells']}
    for x, z in (() if spec.get('asteroid') else foundation):
        draw.rectangle((pad+x*scale, pad+(h-z-1)*scale,
                        pad+(x+1)*scale, pad+(h-z)*scale),
                       fill=('#202e3f' if (x, z) in plan.get('noRoofCells', ()) else
                             '#647f83' if (x, z) in aisles else
                             floor_colors.get((x, z), '#344a58')),
                       outline='#435967')
    colors = {'GravshipHull': '#8799a2', 'DeltaV_DockingPort': '#f0c96e',
              'PilotConsole': '#ef9f55', 'GravEngine': '#d9b569',
              'DeltaV_PrefabPawnMarker': '#f1dce8', 'OxygenPump': '#71c6d0',
              'DeltaV_CargoContainer': '#c49058', 'DeltaV_CargoStorage': '#e0b36c',
              'DeltaV_TradeTerminal': '#6fc99c', 'MineableSteel': '#777f88',
              'MineableComponentsIndustrial': '#b48d62', 'MineablePlasteel': '#819ab4',
              'Vacstone': '#5f5750', 'SmallThruster': '#86aabd', 'LargeThruster': '#86aabd',
              'Door': '#c7ac6b', 'Heater': '#d38864', 'Plant_Rice': '#9abb72', 'SunLamp': '#f0ca75',
              'Bed': '#a6bce9', 'HospitalBed': '#9ddac9', 'ElectricStove': '#9c9fb1',
              'SolarGenerator': '#4269a8', 'Battery': '#a1b4cd', 'DeltaV_SafetyBattery': '#a1b4cd',
              'Cooler': '#69aeba', 'DeltaV_ShipReactor': '#bd8077', 'ChemfuelPoweredGenerator': '#b89b60',
              'DeltaV_PrefabClusterMarker': '#ffb833'}
    for record in records:
        if record['layer'] in ('conduit', 'items'):
            continue
        xs, zs = zip(*record['cells'])
        box = (pad+min(xs)*scale+1, pad+(h-max(zs)-1)*scale+1,
               pad+(max(xs)+1)*scale-1, pad+(h-min(zs))*scale-1)
        color = colors.get(record['name'], '#b6b9b5')
        if record['name'] == 'GravshipHull' and tuple(record['root']) in armor:
            color = '#c1a3bd'
        elif record['name'].startswith('DeltaV_Weapon_'):
            color = '#d58555'
        elif 'Shield' in record['name']:
            color = '#6e9ed3'
        if 'mirrorAxis' in plan and 'exclusionSize' in record:
            color = ('#83d0a2', '#86aabd', '#b097d5', '#86aabd')[record['rotation']]
        draw.rectangle(box, fill=color)
        if record['name'] in ('DeltaV_DockingPort', 'SmallThruster', 'LargeThruster'):
            cx, cy = (box[0] + box[2])/2, (box[1] + box[3])/2
            dx, dz = FACING[record['rotation']]
            tip = (cx + dx*scale*.36, cy - dz*scale*.36)
            draw.polygon([tip, (cx - dx*scale*.22 + dz*scale*.24, cy + dz*scale*.22 + dx*scale*.24),
                          (cx - dx*scale*.22 - dz*scale*.24, cy + dz*scale*.22 - dx*scale*.24)],
                         fill='#101822')
    font_path = 'C:/Windows/Fonts/msyh.ttc'
    font = lambda size: ImageFont.truetype(font_path, size)
    x = w*scale + 2*pad
    draw.text((x, pad), name, font=font(26), fill='#eef3f5')
    y = pad + 48
    module_labels = {'power': '能源', 'medical': '医疗', 'bonded': '保税仓', 'receiving': '收发货',
                     'exchange': '交易大厅', 'lounge': '娱乐休息', 'farm-west': '西温室', 'farm-east': '东温室',
                     'dining': '食堂', 'kitchen': '餐厨', 'residence-west': '西宿舍', 'residence-east': '东宿舍',
                     'logistics': '物资库', 'fabrication': '装配', 'control': '调度与医务',
                     'repair': '维修与舱外作业', 'machining': '机加工', 'casting': '冶锻与制衣', 'dispatch': '成品发运'}
    module_labels.update({'cockpit': '驾驶舱', 'bridge': '舰桥', 'engineering': '机舱',
                          'battery': '炮舱', 'galley': '餐厨与水培', 'habitation': '乘员舱', 'operations': '作战指挥舱',
                          'weapons': '武器舱', 'magazine': '火力与弹药舱'})
    module_labels.update({f'residence-{letter}': f'{letter.upper()} 舱宿舍' for letter in 'abcdef'})
    for index, (key, cells) in enumerate(modules.items(), 1):
        north = max(z for _, z in cells)
        west = min(x for x, z in cells if z == north)
        bx = pad + west*scale + 4
        by = pad + (h-1-north)*scale + 4
        draw.rounded_rectangle((bx, by, bx+24, by+20), radius=3, fill='#141d29')
        draw.text((bx+12, by+10), f'{index:02}', font=font(13), anchor='mm', fill='#eef3f5')
        label = f'{index:02}  {module_labels[key]}'
        if 'mirrorAxis' in plan:
            label += f' · {len(cells)} 格'
        draw.text((x, y), label, font=font(16), fill='#e1e8ee')
        y += 24
    if modules:
        y += 12
    if 'mirrorAxis' in plan:
        if armor:
            draw.text((x, y), '紫灰格 · 局部附加装甲', font=font(16), fill='#c1a3bd')
            y += 24
        if guards:
            radii = '／'.join(map(str, guards))
            draw.text((x, y), f'守卫盾半径 · {radii} 格', font=font(16), fill='#a0c3ec')
            y += 24
        if aisles:
            draw.text((x, y), '蓝灰格 · 固定通路', font=font(16), fill='#9eb8b9')
            y += 24
        propulsion = Counter(r['rotation'] for r in records if 'exclusionSize' in r)
        for label, count, color in (('主推', propulsion[0], '#83d0a2'),
                                    ('制动', propulsion[2], '#b097d5'),
                                    ('侧推', propulsion[1] + propulsion[3], '#86aabd')):
            draw.text((x, y), f'{label}  {count}', font=font(16), fill=color)
            y += 24
        y += 12
    for key, value in sorted(counts.items(), key=lambda p: (-p[1], p[0]))[:15]:
        draw.text((x, y), f'{key}: {value}', font=font(16), fill='#c5d1d7')
        y += 24
    draw.text((x, image_height-28), 'North up · prefab/diagram same records',
              font=font(15), fill='#95aebc')
    preview = generated / (name + '.png')
    image.save(preview)
    if not spec.get('asteroid'):
        with tempfile.NamedTemporaryFile(dir=here, suffix='.png', delete=False) as output:
            temporary = Path(output.name)
            output.write(preview.read_bytes())
        try:
            temporary.replace(here / (name + '.png'))
        finally:
            temporary.unlink(missing_ok=True)
