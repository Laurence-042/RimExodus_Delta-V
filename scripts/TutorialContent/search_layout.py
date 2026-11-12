"""Adapter from a solved module arrangement to the shared prefab authoring pipeline."""
from dataclasses import replace
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from module_assemblies import validate as validate_ship
from module_layout import TITLES, adjacent, flood, furnish as furnish_module, prepare_state, shape
from ship_search import DIR, GALLEY, REFUEL, bounds, move, translate, turn
from ship_propulsion import MountTopology


class SearchLayout:
    def __init__(self, packed, route, target, symmetry_reference=None, player_shell=False, outside_temp=-100,
                 reserved=frozenset(), deck_reserved=frozenset(), task=False,fixed_corridor=False,
                 trimmable=frozenset(), armour_band=None):
        spine = set(route.spine)
        approaches = set().union(*(p.approaches for p in packed['placements']))
        # Carve the connected route between the used landings. Trim unsupported
        # dead branches instead of building them merely to match the silhouette.
        terminals = {min(spine,key=lambda p:(abs(p[0]-x)+abs(p[1]-z),p))
                    for x,z in approaches | {route.port}}
        while tails := {c for c in spine-terminals if len(adjacent({c}) & spine) == 1}:
            spine -= tails
        floor = frozenset((x+dx,z+dz) for x,z in spine for dx in (-1,0,1) for dz in (-1,0,1))
        if not route.lane and approaches <= floor and not any(p.cells & floor for p in packed['placements']):
            degree = [len(adjacent({c}) & spine) for c in spine]
            topology = ('Cross' if 4 in degree else 'Junction' if 3 in degree else
                        'Spine' if len({x for x,_ in spine}) == 1 or len({z for _,z in spine}) == 1 else 'Elbow')
            route = replace(route,spine=frozenset(spine),floor=floor,topology=topology)
        self.packed, self.route, self.target = packed, route, target
        self.symmetry_reference = target if symmetry_reference is None else symmetry_reference
        self.engines = []
        self.player_shell, self.outside_temp = player_shell, outside_temp
        self.reserved = reserved
        self.deck_reserved = deck_reserved
        self.task = task
        self.fixed_corridor = fixed_corridor
        self.trimmable = trimmable
        self.armour_band = armour_band

    def shape(self, name, footprint, prop, exhaust):
        fixtures, shared, modules, optional_walls = {}, {}, [], set()
        for p in self.packed['placements']:
            local = shape(p.module, GALLEY if p.module == 'Galley' else None)
            w, h = local['size']
            modules.append((p.module, p.cells))
            occupied = {}
            if self.task:
                for edge in local['edges'].values():
                    if edge['kind'] == 'optional':
                        optional_walls.update(translate(turn(edge['cells'],w,h,p.rot),p.x,p.z) & p.wall)

            def collect(thing, x, z, r=0, stuff=None, layer='body', state=None):
                if layer != 'body':
                    return
                cells = footprint(thing, x, z, r)[2]
                occupied.update((c, dict(name=thing, transmitsPower=False)) for c in cells)
                cells = translate(turn(cells, w, h, p.rot), p.x, p.z)
                if cells & p.cut:
                    assert cells <= p.cut and thing == 'GravshipHull'
                    return
                a, b, _, _ = bounds(cells)
                r = (r + p.rot) % 4 if prop(thing, 'rotatable', 'true').lower() == 'true' else 0
                assert footprint(thing,a,b,r)[2] == cells, (thing,'rotated footprint')
                if thing == 'Vent':
                    state = {'switchOn': 'True', 'wantSwitchOn': 'True'}
                if thing in ('ChemfuelTank', 'LargeChemfuelTank'):
                    capacity = prop(thing, REFUEL + 'fuelCapacity')
                    state = {'fuel': capacity, 'configuredTargetFuelLevel': capacity}
                if self.task and thing == 'DeltaV_ShieldGenerator':
                    state = dict(state or {}, DeltaV_shieldMode='Guard', DeltaV_regularCapacity=750,
                                 DeltaV_guardUnlocked='True')
                for cell in cells:
                    if cell in fixtures:
                        assert thing == fixtures[cell][0] == 'GravshipHull', (name,'overlapping fixture',cell)
                        shared[cell] = True
                    fixtures[cell] = (thing, a, b, r, stuff, state)

            furnish_module(p.module, local, collect, footprint, prop, local['cells'], local['cells'], [], occupied)
            if p.module == 'Galley':
                used = sum(f['name'] != 'StandingLamp' for f in occupied.values())
                assert 100*(len(local['cells'])-used) <= 40*len(local['cells']), 'galley empty foundation'
        floor, port = self.route.floor, self.route.port
        module_cells = set().union(*(cells for _,cells in modules))
        if self.player_shell and not self.fixed_corridor:
            # Side pockets use the leftover space after module packing. The
            # lane remains intact and each pocket gets its own sealed border.
            floor = set(floor)
            perimeter = adjacent(floor)-module_cells-{port}
            bays = []
            for x,z in sorted(perimeter):
                for dx,dz in ((1,0),(0,1)):
                    bay = {(x,z),(x+dx,z+dz)}
                    outer = adjacent(bay)
                    if bay <= perimeter and (bay | outer) <= self.target and not outer & ({port} | module_cells):
                        score = (len({c for c in outer if adjacent({c}) <= self.target}),
                                 -len(outer & module_cells))
                        bays.append((score,(x,z),bay))
            for _,__,bay in sorted(bays,key=lambda row:(row[0],row[1]),reverse=True):
                if len(floor-self.route.floor) >= 16:
                    break
                if not bay & floor and not adjacent(bay) & (floor-self.route.floor):
                    floor |= bay
            floor = frozenset(floor)
        hull = floor | adjacent(floor) | module_cells
        reserved_outer = (set().union(*(slot[1] for slot in self.armour_band.values()))
                          if self.armour_band is not None else set())
        walls = (adjacent(floor) - floor - fixtures.keys() - {port}) | {
            c for c,r in fixtures.items() if r[0] == 'GravshipHull'}
        # Where a corridor shoulder touches a module's external wall, that wall
        # is also the corridor boundary. Never put a second skin in front of it.
        standalone = walls - fixtures.keys()
        merged = {c for c in standalone if adjacent({c}) <= hull and
                  any(move(c,d) in floor and move(c,d,-1) in walls - standalone for d in DIR)}
        floor = floor | merged
        walls -= merged
        service = set()
        engine_banks = []
        if self.player_shell:
            # When a corridor hugs a module, its wall is the pressure boundary.
            # Fill only the exposed flank of the absorbed corridor wall.
            exterior = {move(port,DIR[self.route.rotation],i) for i in range(1,5)}
            blocked_flanks = exterior | reserved_outer
            for c in sorted((walls - fixtures.keys())):
                if not any(move(c,d) in floor and move(c,d,-1) in walls & fixtures.keys() for d in DIR):
                    continue
                flank = adjacent({c}) - hull
                if flank <= self.target and not flank & blocked_flanks:
                    hull |= flank
                    walls |= flank
                    walls.discard(c)
                    floor |= {c}
            for c in sorted(walls-fixtures.keys()):
                for d in DIR:
                    other = move(c,d)
                    if (c not in walls or other not in walls-fixtures.keys() or
                        move(c,d,-1) not in floor or move(other,d) not in floor):
                        continue
                    flank = adjacent({c,other})-hull
                    if flank <= self.target and not flank & blocked_flanks:
                        hull |= flank
                        walls |= flank
                        walls -= {c,other}
                        floor |= {c,other}
            if self.task and not self.fixed_corridor:
                hull,walls,service,engine_banks = self.end_banks(hull,walls,fixtures,footprint,exhaust)
            if not self.fixed_corridor:
                hull,walls,alcoves = self.engine_alcoves(hull,walls,fixtures,shared,prop,footprint,exhaust)
                service |= alcoves
            # An alcove can introduce a second wall in front of a room's wall.
            # Keep the room boundary and open the non-module side only if every
            # exposed flank can be sealed on the existing foundation.
            removable = (walls-fixtures.keys()) | (optional_walls & walls - shared.keys())
            for c in sorted(removable) if self.task else ():
                for d in DIR:
                    other = move(c,d)
                    if not (c in walls and other in walls and move(c,d,-1) in hull-walls and
                            move(other,d) in hull-walls):
                        continue
                    flank = adjacent({c})-hull
                    if flank <= self.target-self.deck_reserved and not flank & blocked_flanks:
                        walls.remove(c)
                        hull |= flank
                        walls |= flank
                        floor |= {c}
                        break
            for c in sorted(self.reserved & (walls-fixtures.keys())) if self.task else ():
                flank = adjacent({c})-hull
                if adjacent({c}) & floor and flank <= self.target and not flank & blocked_flanks:
                    hull |= flank
                    walls = (walls | flank) - {c}
                    floor |= {c}
        assert port in hull and port not in fixtures
        walkable = (hull - walls - {c for c,r in fixtures.items()
                    if r[0] != 'Door' and prop(r[0], 'passability', 'Standable') == 'Impassable'})
        engine_cells = set().union(*(e['cells'] for e in self.engines))
        foundation = hull | engine_cells
        assert flood(port, foundation) == foundation
        if self.player_shell:
            assert foundation <= self.target
        self.mount_inputs = (hull, walls - shared.keys(), walkable)
        a,b,max_x,max_z = bounds(foundation | self.target)
        assert a >= 0 and b >= 0
        seams = []
        joins = {p.join[:2] for p in self.packed['placements'] if p.join}
        for first,second in sorted(joins,key=lambda v:(min(v[0]),min(v[1]))):
            f,s = min(first),min(second)
            if f < s:
                seams.append((int(f[0] == s[0]),second))
        trim = set().union(*(e.get('trim',()) for e in self.engines))
        rim = set()
        if self.fixed_corridor:
            available_rim = self.trimmable - hull - adjacent(hull) - exterior
            for c in sorted(available_rim):
                if len(rim) >= min(60,len(self.target)//12):
                    break
                if adjacent({c}) <= self.target:
                    continue
                remaining = self.target-rim-{c}
                if flood(port,remaining) == remaining:
                    rim.add(c)
        envelope = self.target-trim-rim if self.player_shell else foundation
        assert flood(port,envelope) == envelope, ('PROPULSION','exhaust cut would disconnect the hull')
        armor = set()
        if self.fixed_corridor:
            exhaust_cells = set().union(*(e['exclusion'] for e in self.engines))
            excluded = self.reserved | self.deck_reserved | exterior | engine_cells | exhaust_cells
            armor = {move(c,d) for c in walls for d in DIR if move(c,d,-1) in hull-walls
                     and move(c,d) in envelope-hull-excluded and move(c,d,2) not in hull}
        return dict(size=(max_x+1,max_z+1), cells=envelope,
                    noRoofCells=engine_cells | (envelope-foundation if self.player_shell else set()),
                    fixtures=fixtures, walls=walls | armor, armor=armor, rim=rim,
                    sharedCells=shared, modules=modules,
                    corridor=self.route.floor if self.fixed_corridor else floor,
                    hull=hull, openSeams=seams, engineService=service, engineBanks=engine_banks,
                    spec=dict(modules=[(p.module,p.x,p.z,p.rot) for p in self.packed['placements']]))

    def end_banks(self, hull, walls, fixtures, footprint, exhaust):
        """Use a shared, enclosed service strip behind a row of small nozzles at a narrow end."""
        hull,walls = set(hull),set(walls)
        service, banks = set(), []
        x0,z0,x1,z1 = bounds(self.target)
        spans = x1-x0+1,z1-z0+1
        axis = 1 if spans[1] > spans[0] else 0
        if spans[axis] < 3*spans[1-axis]:
            return hull,walls,service,banks
        forbidden = self.reserved | self.deck_reserved | {
            move(self.route.port,DIR[self.route.rotation],i) for i in range(5)}
        for r,inward in enumerate(DIR):
            if not inward[axis]:
                continue
            outward = DIR[(r+2)%4]
            tangent = DIR[(r+1)%4]
            slots = {}
            for c in sorted(self.target-hull-forbidden):
                bank,tip = move(c,outward),move(c,outward,2)
                if not {bank,tip} <= self.target-hull-forbidden:
                    continue
                x,z,_,_ = bounds({bank,tip})
                _,root,foot = footprint('SmallThruster',x,z,r)
                if foot == {bank,tip} and not set(exhaust('SmallThruster',root,r)) & (self.target | forbidden):
                    slots[c] = (bank,tip)
            choices = []
            for mouth in sorted(walls-fixtures.keys()):
                inside = move(mouth,inward)
                if inside not in hull-walls or inside in fixtures:
                    continue
                for distance in range(1,7):
                    center = move(mouth,outward,distance)
                    if center in hull or center not in self.target-forbidden:
                        break
                    if center not in slots:
                        continue
                    row = {center}
                    for sign in (-1,1):
                        for offset in range(1,spans[1-axis]):
                            cell = move(center,tangent,sign*offset)
                            if cell not in slots or (adjacent({cell}) & hull)-{mouth}:
                                break
                            row.add(cell)
                    lane = row | {move(mouth,outward,i) for i in range(distance)}
                    if len(row) < 4 or (adjacent(lane-{mouth}) & hull)-{mouth}:
                        continue
                    border = adjacent(lane)-hull
                    faces = {slots[c][0] for c in row}
                    tips = {slots[c][1] for c in row}
                    if not border <= self.target-forbidden or border & tips:
                        continue
                    if any(move(c,d) in walls-{mouth} and move(c,d,2) in hull-walls
                           for c in border for d in DIR if move(c,d,-1) in lane):
                        continue
                    choices.append((-len(row),distance,mouth,lane,border,faces))
            if choices:
                _,_,mouth,lane,border,faces = min(choices,key=lambda choice:choice[:3])
                hull |= lane | border
                walls = (walls | border)-lane
                service |= lane
                banks.append((r,faces))
        return hull,walls,service,banks

    def engine_alcoves(self, hull, walls, fixtures, shared, prop, footprint, exhaust):
        """Extend maintenance pockets to the existing shell, leaving the exhaust outside it."""
        hull,walls = set(hull),set(walls)
        service = set()
        approach = {move(self.route.port,DIR[self.route.rotation],i) for i in range(5)}
        for r,inward in enumerate(DIR):
            tangent = DIR[(r+1)%4]
            candidates = []
            for c in sorted(walls-shared.keys()):
                mouth = {c,move(c,tangent)}
                if not mouth <= walls-shared.keys():
                    continue
                inside = {move(cell,inward) for cell in mouth}
                if not any(q in hull-walls and q not in fixtures for q in inside):
                    continue
                outward = DIR[(r+2)%4]
                for distance in range(1,8):
                    bank = {move(cell,outward,distance) for cell in mouth}
                    if bank & hull or not bank <= self.target:
                        break
                    tip = {move(cell,outward) for cell in bank}
                    if not tip <= self.target or tip & hull:
                        continue
                    x,z,_,_ = bounds(bank | tip)
                    _,root,foot = footprint('LargeThruster',x,z,r)
                    if foot != bank | tip or set(exhaust('LargeThruster',root,r)) & (self.target | approach):
                        continue
                    inner = {move(cell,outward,i) for i in range(distance) for cell in mouth}
                    cover = adjacent(inner) - inner - hull
                    if not cover <= self.target or (cover | inner | foot) & approach:
                        continue
                    if (adjacent(inner-mouth) & hull) - mouth:
                        continue
                    candidates.append((distance,c,inner,cover,bank))
                    break
            selected = []
            for _,c,inner,cover,bank in sorted(candidates,key=lambda v:(v[0],v[1])):
                if any(abs(c[0]-a)+abs(c[1]-b) < 4 for a,b in selected):
                    continue
                if (inner | cover) & service or (bank & hull):
                    continue
                opened = inner & walls
                if len(opened) != 2:
                    continue
                if any(move(q,d) in walls-opened and move(q,d,2) in hull-walls
                       for q in cover for d in DIR if move(q,d,-1) in inner):
                    continue
                hull |= cover | inner
                walls = (walls | cover) - opened
                service |= inner
                selected.append(c)
                if len(selected) == 3:
                    break
        return hull,walls,service

    def furnish(self, name, plan, place, footprint, prop, foundation, roofs, records, occupied):
        for cell, (thing,x,z,r,stuff,state) in sorted(plan['fixtures'].items()):
            if thing != 'GravshipHull' and cell == (x,z):
                place(thing,x,z,r,stuff,state=state)
        for e in self.engines:
            record = place(e['def'], *e['origin'], e['rotation'],
                           state={'DeltaV_powerPercent':e.get('power',100)})
            assert record['airtight'] and record['holdsRoof']
            record['replacedWallCells'] = sorted(e['face'])
        roofs.difference_update(plan['noRoofCells'])
        port = self.route.port
        place('DeltaV_DockingPort', *port, self.route.rotation)
        for c in sorted(plan['walls'] - occupied.keys()):
            place('GravshipHull', *c)
        walls = {c for c,r in occupied.items() if r['name'] == 'GravshipHull' or r['name'].endswith('Thruster')}
        protected = ((self.route.lane or self.route.spine) | plan['engineService'] |
                     set().union(*(p.approaches for p in self.packed['placements']))
                     | {move(port,DIR[self.route.rotation],-1)} | self.reserved)
        if self.fixed_corridor:
            for axis,cells in plan['openSeams']:
                protected |= cells | {move(c,DIR[3 if axis == 0 else 2]) for c in cells}
        plan['oxygenPerSecond'] = 100 * float(prop('OxygenPump',
            "comps/li[@Class='CompProperties_OxygenPusher']/airPerSecondPerHundredCells", '0'))
        if self.player_shell and self.engines and hasattr(self,'life_support'):
            fixtures,plan['localHeaters'] = self.life_support
            for thing,x,z,r,state in fixtures:
                place(thing,x,z,r,state=state)
        else:
            first = len(records)
            candidates = sorted(plan['corridor'] - protected)
            local = set()
            walk = set()
            start = move(port,DIR[self.route.rotation],-1)
            if self.fixed_corridor:
                work = set()
                for record in records:
                    if record['hasInteraction']:
                        x,_,z = record['interaction']
                        dx,dz = ((x,z),(z,-x),(-x,-z),(-z,x))[record['rotation']]
                        a,b = record['root']
                        work.add((a+dx,b+dz))
                for p in self.packed['placements']:
                    work.update(move(c,DIR[p.rot]) for c in p.doors | p.vents)
                walk = flood(start,roofs-{c for c,r in occupied.items() if
                                          r['passability'] != 'Standable' and r['name'] != 'Door'})
                local = walk-protected-work-occupied.keys()-plan['corridor']
            def stays_clear(cells):
                return not self.fixed_corridor or walk-cells <= flood(start,walk-cells)
            battery = next(((x,z,r) for x,z in candidates for r in (0,1) if footprint('Battery',x,z,r)[2] <=
                            plan['corridor'] - protected - occupied.keys()), None)
            if battery is None and local:
                battery = next(((x,z,r) for x,z in sorted(local) for r in (0,1)
                                if (cells := footprint('Battery',x,z,r)[2]) <= local and stays_clear(cells)),None)
            assert battery, 'no connected corridor battery bay'
            place('Battery',*battery,state={'storedPower':float(prop('Battery',
                  "comps/li[@Class='CompProperties_Battery']/storedEnergyMax"))})
            if self.fixed_corridor:
                walk -= footprint('Battery',*battery)[2]
            # Place devices along the free service shoulders, with pump outlet on
            # a wall. Leave the route center and all module landing cells clear.
            devices = [('OxygenPump', 5, 5), ('Heater', 4, 6)]
            if self.player_shell:
                pressure_cells = len(roofs - {c for c,r in occupied.items() if r['airtight']})
                minimum = max(1, -(-pressure_cells // (120 * plan['oxygenPerSecond'])))
                devices = [('OxygenPump', 0, int(minimum))]
            for thing, spacing, limit in devices:
                done = []
                while len(done) < limit:
                    options = [(c,r) for c in candidates if c not in occupied
                               for r,d in enumerate(DIR) if move(c,d) in walls]
                    if not options and self.fixed_corridor:
                        options = [(c,r) for c in sorted(local-occupied.keys()) if stays_clear({c})
                                   for r,d in enumerate(DIR) if move(c,d) in walls]
                    if not options:
                        break
                    def spread(option):
                        (x,z),r = option
                        return (min((abs(x-a)+abs(z-b) for a,b in done), default=100),
                                -min(abs(x-a)+abs(z-b) for a,b in self.route.spine), -x, -z, -r)
                    c,r = max(options,key=spread)
                    if done and spread((c,r))[0] < spacing:
                        break
                    place(thing,*c,r if thing == 'OxygenPump' else 0,
                          state={'targetTemperature':21} if thing == 'Heater' else None)
                    if self.fixed_corridor:
                        walk.discard(c)
                    done.append(c)
                assert done and (not self.player_shell or len(done) == limit), (thing,'no viable corridor mount')
            if self.player_shell:
                self.supplement_heat(plan, occupied, records, roofs, protected, place, prop)
                self.life_support = ([(r['name'],*r['origin'],r['rotation'],r['stateOverrides'])
                                      for r in records[first:]],plan['localHeaters'])
        if not self.player_shell:
            for cell in sorted(self.route.spine - occupied.keys())[:2]:
                place('DeltaV_PrefabPawnMarker',*cell,layer='marker')
        for cell in sorted((plan['hull'] if self.player_shell else foundation) -
                           {c for c,r in occupied.items() if r['transmitsPower']}):
            place('HiddenConduit',*cell,layer='conduit')
        return walls, move(port, DIR[self.route.rotation], -1)

    def supplement_heat(self, plan, occupied, records, roofs, protected, place, prop):
        # Estimate the native thin-roof loss and vent transfer in vacuum at
        # 21 C in the corridor and at least 18 C in rooms. Include a cold start.
        heat = float(prop('Heater',"comps/li[@Class='CompProperties_TempControl']/energyPerSecond"))
        cells = {c for c in roofs if not occupied.get(c,{}).get('airtight')
                 and occupied.get(c,{}).get('name') != 'Vent'}
        vents = [r for r in records if r['name'] == 'Vent']
        forbidden = set(protected)
        for record in records:
            if record['hasInteraction']:
                x,_,z = record['interaction']
                dx,dz = ((x,z),(z,-x),(-x,-z),(-z,x))[record['rotation']]
                a,b = record['root']
                forbidden.add((a+dx,b+dz))
        walk = cells | {c for c,r in occupied.items() if r['name'] in ('Door','DeltaV_DockingPort')}
        walk -= {c for c,r in occupied.items() if r['passability'] != 'Standable'
                 and r['name'] not in ('Door','DeltaV_DockingPort')}
        start = move(self.route.port,DIR[self.route.rotation],-1)
        walk = flood(start,walk)
        for p in self.packed['placements']:
            inward = DIR[p.rot]
            forbidden.update(move(c,inward) for c in p.doors | p.vents)
        remaining, rooms = set(cells), []
        while remaining:
            room = flood(min(remaining),remaining)
            remaining -= room
            rooms.append((bool(room & self.route.spine),room))
        main = [room for central,room in rooms if central]
        assert len(main) == 1, ('HEATING','corridor is not one pressure zone')
        corridor = main[0]
        central_sides = set()
        section_width = {}
        for c,w,_,_ in self.route.sections:
            section_width[c] = max(w,section_width.get(c,0))
        if self.route.width > 1:
            for c in self.route.spine:
                directions = adjacent({c}) & self.route.spine
                if len(directions) != 2:
                    continue
                axis = tuple(sum(p[i]-c[i] for p in directions) for i in (0,1))
                if axis != (0,0):
                    continue
                local_width = section_width.get(c,self.route.width)
                if local_width == 1:
                    continue
                directions = DIR[:2] if not section_width and local_width == 2 else DIR
                sites = []
                for d in directions:
                    site = move(c,d)
                    if site not in self.route.spine and site in self.route.lane-self.route.clear and (
                            occupied.get(move(site,d),{}).get('airtight')):
                        sites.append(site)
                central_sides.update(sites[:local_width-1])
        access = self.route.clear | self.reserved | {start} | set().union(
            *(p.approaches for p in self.packed['placements']))
        # Equipment never uses the door-side path through a turn or a junction.
        central_sides -= access | {c for c in forbidden if c not in self.route.lane}
        central_sides -= adjacent({c for r in vents for c in r['cells']})
        def heat_loss(room,target):
            delta = max(0.,target-self.outside_temp)
            return len(room)*((min(100,delta)+5*max(0,delta-100))*.00005*60 + 18/1800)
        transfer = 14*3/2/(250/60)
        links = {min(room):sum(transfer for vent in vents
                              if any(move(tuple(vent['root']),DIR[vent['rotation']],s) in room and
                                     move(tuple(vent['root']),DIR[vent['rotation']],-s) in corridor
                                     for s in (-1,1)))
                 for central,room in rooms if not central}
        corridor_demand = heat_loss(corridor,21)
        locally_fed = self.route.width == 1 or (bool(self.route.sections) and not central_sides)
        required_walk = walk & (access | (forbidden-self.route.lane) | {c for c,r in occupied.items()
                                                                      if r['name'] == 'Door'})
        def valid(c, from_walk=None):
            from_walk = walk if from_walk is None else from_walk
            reachable = flood(start,from_walk-{c})
            return required_walk <= reachable and all(
                adjacent(set(r['cells'])) & reachable for r in records if r['name'] in (
                    'Heater','Vent','Shelf','ShelfSmall','DeltaV_CargoStorage','OxygenPump') and
                adjacent(set(r['cells'])) & from_walk)
        corridor_target = 21
        if self.task:
            # A tightly furnished room need not be a heat source. A heated
            # neighboring room can feed the corridor, which then warms it.
            local_capacity = {}
            for central,room in rooms:
                if central:
                    continue
                trial = set(walk)
                slots = sum(occupied.get(c,{}).get('name') == 'Heater' for c in room)
                for c in sorted((room & walk)-forbidden-occupied.keys(),
                                key=lambda p:(len(adjacent({p}) & walk),p)):
                    if valid(c,trial):
                        slots += 1
                        trial.discard(c)
                        if slots >= 3:
                            break
                local_capacity[min(room)] = slots*heat
            recipients = {min(room):heat_loss(room,18) for central,room in rooms
                          if not central and local_capacity[min(room)] == 0}
            # Vent transfer scales with temperature difference. Reserve a
            # warmer passage only if a densely furnished room needs it.
            corridor_target = max(21,max((18+3*v/max(links[k],1e-6) for k,v in recipients.items()),default=21))
            assert corridor_target <= 28, (
                'HEATING','room without a legal heater cannot be warmed through its vents')
            donors = {min(room):min(2*links[min(room)],
                                   max(0,local_capacity[min(room)]-heat_loss(room,corridor_target+6)))
                      for central,room in rooms if not central}
            corridor_demand = heat_loss(corridor,corridor_target)
            if self.route.sections and not locally_fed:
                need = corridor_demand+sum(min(heat_loss(room,18),links[min(room)]*(corridor_target-18)/3)
                                           for central,room in rooms if not central)
                trial = set(walk)
                central_capacity = 0.
                for c in sorted((corridor & trial & central_sides)-occupied.keys(),
                                key=lambda p:(len(adjacent({p}) & walk),p)):
                    if valid(c,trial):
                        trial.discard(c)
                        central_capacity += heat
                        if central_capacity >= need:
                            break
                if central_capacity < need and corridor_demand+sum(recipients.values()) <= sum(donors.values()):
                    locally_fed = True
            if locally_fed:
                corridor_demand += sum(recipients.values())
                assert corridor_demand <= sum(donors.values()), ('HEATING','not enough room vents to warm corridor')
            else:
                links = {k:v*(corridor_target-18)/3 for k,v in links.items()}
        elif locally_fed:
            assert corridor_demand <= sum(links.values()), ('HEATING','not enough room vents to warm corridor')
        exported = 0.
        plan['localHeaters'] = []
        for central,room in sorted(rooms,key=lambda item:(item[0],min(item[1]))):
            if central:
                if locally_fed:
                    continue
                demand,target = corridor_demand+exported,corridor_target
            elif locally_fed:
                if self.task:
                    if min(room) in recipients:
                        continue
                    capacity = donors.get(min(room),0)
                    target = corridor_target+6 if capacity else 18
                    supply = sum(donors.values())
                else:
                    capacity = links[min(room)]
                    target = 24 if capacity else 21
                    supply = sum(links.values())
                demand = heat_loss(room,target) + corridor_demand*capacity/max(supply,1e-6)
            else:
                target = 21
                demand = heat_loss(room,18)
                passive = min(demand,links[min(room)])
                exported += passive
                demand -= passive
            demand -= heat * sum(occupied.get(c,{}).get('name') == 'Heater' for c in room)
            while demand > 1e-6:
                options = ((room & walk & central_sides) if central else ((room & walk)-forbidden)) - occupied.keys()
                site = next((c for c in sorted(options,key=lambda c:(len(adjacent({c}) & walk),c))
                             if len(adjacent({c}) & walk) > 0 and valid(c)),None)
                assert site is not None, ('HEATING','no legal corridor heater site' if central else
                                          'no legal room heater site',min(room),len(room),len(room & walk),
                                          len(options),round(demand,2))
                place('Heater',*site,state={'targetTemperature':target})
                if not central:
                    plan['localHeaters'].append(site)
                walk.discard(site)
                demand -= heat

    prepare_state = staticmethod(prepare_state)
    def validate(self, name, plan, records, foundation, roofs, reachable):
        report,bodies = validate_ship(name,plan,records,foundation,roofs,reachable)
        pump_count = sum(r['name'] == 'OxygenPump' for r in records)
        air = {c for c in roofs if not bodies.get(c,{}).get('airtight',False)}
        air_volume = len(air)
        hull,walls,walkable = self.mount_inputs
        self.mount_inputs = hull,walls,walkable & air & reachable
        if self.task:
            interior = (air - {c for c,r in bodies.items() if r['passability'] == 'Impassable'}) | {
                c for c,r in bodies.items() if r['name'] in ('Door','DeltaV_DockingPort')}
            start = move(self.route.port,DIR[self.route.rotation],-1)
            self.mount_inputs = hull,walls,walkable & air & flood(start,interior)
            required = set(self.route.clear)
            for record in records:
                if record['hasInteraction']:
                    x,_,z = record['interaction']
                    dx,dz = ((x,z),(z,-x),(-x,-z),(-z,x))[record['rotation']]
                    a,b = record['root']
                    required.add((a+dx,b+dz))
            vents = {c for c,r in bodies.items() if r['name'] == 'Vent'}
            self.mount_topology = MountTopology(foundation,start,air | vents,interior,required,vents,
                                               reserved_band=self.armour_band is not None)
            self.engine_banks = plan['engineBanks']
        if self.player_shell:
            protected = set(plan['corridor'])
            for cell,record in bodies.items():
                if record['name'] in ('Door','Vent'):
                    protected.update(adjacent({cell}))
                if record['hasInteraction']:
                    x,_,z = record['interaction']
                    dx,dz = ((x,z),(z,-x),(-x,-z),(-z,x))[record['rotation']]
                    a,b = record['root']
                    protected.add((a+dx,b+dz))
            self.inset = (air & reachable) - bodies.keys() - protected
        supply = pump_count * plan['oxygenPerSecond']
        assert supply > 0 and air_volume/supply <= 120, (name,'oxygen pump volume budget')
        report.update(pumps=pump_count, heatedAirCells=air_volume,
                      idealRepressurizationSeconds=air_volume/supply,
                      heaters=sum(r['name'] == 'Heater' for r in records))
        if self.player_shell:
            grid = {c for r in records if r['transmitsPower'] for c in r['cells']}
            assert grid and flood(min(grid),grid) == grid, ('POWER','disconnected transmitters')
            assert all(any((x+dx,z+dz) in grid for dx in range(-6,7) for dz in range(-6,7))
                       for r in records if r['powerWatts'] or r['name'] in ('Battery','DeltaV_SafetyBattery')
                       for x,z in (r['root'],)), ('POWER','device without a grid connection')
            assert self.route.clear <= reachable and all(c not in bodies for c in self.route.clear), (
                name,'blocked corridor through lane')
            assert all(adjacent(set(r['cells'])) & reachable for r in records if r['name'] == 'Heater'), (
                name,'inaccessible heater')
            report['localHeaters'] = sorted(plan['localHeaters'])
            rooms = set().union(*(cells for _,cells in plan['modules']))
            fixtures = set(bodies)
            circulation = (set(self.route.floor) | self.reserved | plan['engineService']) - rooms - fixtures
            air_boundary = adjacent(air)
            boundary = {c for c in fixtures-rooms if c in air_boundary or
                        bodies[c]['name'].endswith('Thruster') or c in self.route.floor}
            boundary |= plan['armor']
            unused = foundation - (rooms | circulation | boundary | self.deck_reserved)
            uncovered = set(unused)
            largest = 0
            while uncovered:
                block = flood(min(uncovered),uncovered)
                largest = max(largest,len(block))
                uncovered -= block
            report['deck'] = dict(unused=sorted(unused),largestUnused=largest,
                noRoofOrFixture=len(foundation-roofs-fixtures),rooms=sorted(rooms),
                circulation=sorted(circulation),boundary=sorted(boundary),reserved=sorted(self.deck_reserved))
            if self.task:
                report['flightRotation'] = next(r['rotation'] for r in records if r['name'] == 'PilotConsole')
        return report,bodies

    def write_preview(self, name, plan, report, bodies, generated):
        mask, actual = self.target, plan['cells']
        report.update(reference=sorted(mask), overlap=len(mask & actual)/len(mask | actual),
                      corridor=sorted(self.route.floor), centerline=sorted(self.route.spine),
                      topology=self.route.topology, corridorWidth=self.route.width,
                      corridorLane=sorted(self.route.lane), corridorClear=sorted(self.route.clear),
                      corridorSections=self.route.sections,
                      sharedWalls=sorted(plan['sharedCells']),
                      openSeams=[(axis,sorted(cells)) for axis,cells in plan['openSeams']])
        if self.player_shell:
            report['exhaustCutouts'] = sorted(mask-actual-plan['rim'])
        if self.fixed_corridor:
            report['outerArmor'] = sorted(plan['armor'])
            report['rimCutouts'] = sorted(plan['rim'])
        (generated / f'{name}.layout.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
        w,h = plan['size']
        scale, pad = 23, 28
        img = Image.new('RGB',(w*scale+2*pad,h*scale+4*pad),'#111b28')
        draw = ImageDraw.Draw(img)
        font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',14)
        swatches = ('#42665e','#51688e','#705672','#795e49','#496e7c')
        regions = {c:swatches[i%len(swatches)] for i,(_,cells) in enumerate(plan['modules']) for c in cells}
        def pixel(c):
            return pad+c[0]*scale,2*pad+(h-1-c[1])*scale
        unused = {tuple(c) for c in report.get('deck',{}).get('unused',())}
        for c in sorted(actual | mask):
            x,y = pixel(c)
            r = bodies.get(c,{}).get('name','')
            color = ('#253545' if c not in actual else '#273f50' if c in self.route.floor else
                     regions.get(c,'#536473'))
            if r == 'GravshipHull': color = '#6b7685' if c not in plan['sharedCells'] else '#a17ba9'
            elif r == 'Vent': color = '#71b6d4'
            elif r in ('Door','DeltaV_DockingPort'): color = '#d4ad65'
            elif r.endswith('Thruster'): color = '#e8a278' if c in bodies[c]['replacedWallCells'] else '#b16e54'
            elif r in ('Battery','ChemfuelPoweredGenerator'): color = '#e0c68e'
            elif r.endswith('ChemfuelTank'): color = '#94bca1'
            elif r == 'DeltaV_CargoStorage': color = '#86a7c3'
            elif r in ('Heater','OxygenPump'): color = '#87c8b8'
            elif r: color = '#9aabbc'
            if c in unused: color = '#72505b'
            elif c in self.deck_reserved: color = '#526e89'
            draw.rectangle((x,y,x+scale-1,y+scale-1),fill=color,outline='#1c2938')
            if c in self.route.spine:
                draw.ellipse((x+9,y+9,x+13,y+13),fill='#62e0ba')
            for i,d in enumerate(DIR):
                if c in mask and move(c,d) not in mask:
                    segs = ((x,y,x+scale,y),(x+scale,y,x+scale,y+scale),
                            (x,y+scale,x+scale,y+scale),(x,y,x,y+scale))
                    draw.line(segs[i],fill='#e6c269',width=2)
        for module,cells in plan['modules']:
            a,b,c,d = bounds(cells)
            x,y = pixel(((a+c)/2,(b+d)/2))
            draw.text((x+scale/2,y+scale/2), TITLES.get(module,'餐厨'),font=font,
                      anchor='mm',fill='white',stroke_width=2,stroke_fill='#192635')
        for e in self.engines:
            x,y = pixel((e['center'][0]-.5,e['center'][1]-.5))
            x,y = x+scale/2,y+scale/2
            dx,dz = DIR[e['rotation']]
            dy = -dz
            tx,ty = x+dx*7,y+dy*7
            draw.line((x-dx*6,y-dy*6,tx,ty),fill='#331c17',width=2)
            draw.polygon(((tx,ty),(tx-dx*5-dy*4,ty-dy*5+dx*4),
                          (tx-dx*5+dy*4,ty-dy*5-dx*4)),fill='#331c17')
        draw.text((pad,18),f"{name}  ·  {self.route.topology}  ·  IoU {report['overlap']:.1%}",font=font,fill='white')
        draw.text((pad,h*scale+2*pad+10),'金线：参考外形   青点：走廊   橙色：推进器   紫色：共墙',
                  font=font,fill='#c2d3e2')
        if self.task:
            deck = report['deck']
            draw.text((pad,h*scale+2*pad+30),f"暗红：无用途 {len(unused)} 格；最大连续块 {deck['largestUnused']} 格",
                      font=font,fill='#c2d3e2')
        img.save(generated / f'{name}.png')
