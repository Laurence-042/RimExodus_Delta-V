"""Joint mount/command CP-SAT model. The native allocator is the acceptance gate."""
from collections import Counter, defaultdict
import math

from ortools.sat.python import cp_model

from build import material_cost, thruster_force
from module_layout import adjacent, flood
from ship_search import DIR, bounds, move, solver_for


AXES = ('east','west','forward','brake','turnRight','turnLeft')
THRUST = 1  # Force unit used by the integer surrogate; each mount carries its own signed Def force.
QUANTA = 24


def force_vector(mount, facing=0):
    magnitude = round(mount['force']/THRUST)
    return tuple(d*magnitude for d in DIR[(mount['rotation']-facing)%4])


def armour_mount_slots(geometry, hull, protected, trimmable):
    """Enumerate inner wall, outer armour, exhaust and maintenance cells."""
    _, _, footprint, _, exhaust = geometry
    usable = hull-protected
    available = sorted(usable)
    cut_limit = min(60,len(hull)//12)
    trimmable = trimmable-protected
    slots = {}
    for kind in ('LargeThruster','SmallThruster'):
        for rotation,inward in enumerate(DIR):
            _,root,body = footprint(kind,0,0,rotation)
            inner = {c for c in body if move(c,inward) not in body}
            outer = body-inner
            outlets = set(exhaust(kind,root,rotation))
            service = {move(c,inward) for c in inner}
            for x,z in available:
                def at(cells):
                    return frozenset((x+a,z+b) for a,b in cells)
                occupied = at(body)
                room_side = at(service)
                if not occupied <= usable or not room_side & usable:
                    continue
                cut = at(outlets) & hull
                if not cut <= trimmable or len(cut) > cut_limit:
                    continue
                slots[kind,rotation,(x,z)] = (at(inner),at(outer),cut,room_side)
    return slots


def armour_mount_band(slots, route):
    corridor = route.floor | adjacent(route.floor)
    approach = {move(route.port,DIR[route.rotation],n) for n in range(5)}
    inner_blocked, outer_blocked = route.floor | approach, corridor | approach
    return {key:cells for key,cells in slots.items()
            if not (cells[0] & inner_blocked or (cells[1] | cells[2]) & outer_blocked)}


def packed_armour_band(band, route, placements):
    """Keep the reserved strips exposed by the chosen rooms and corridor."""
    rooms = set().union(*(p.cells for p in placements))
    walls = set().union(*(p.wall for p in placements))
    corridor_wall = adjacent(route.floor)-rooms-route.floor-{route.port}
    pressure_wall = walls | corridor_wall
    pressure = rooms | route.floor | corridor_wall | {route.port}
    inside = (rooms-walls) | route.floor
    return {key:cells for key,cells in band.items()
            if cells[0] <= pressure_wall and cells[3] & inside and not (cells[1] | cells[2]) & pressure}


def shell_mount_slots(geometry, hull, trimmable, reserved):
    """Small nozzles give the beam search a concrete set of edge slots to preserve."""
    prop, _, footprint, _, exhaust = geometry
    force = thruster_force(prop, 'SmallThruster')
    clear_masks = defaultdict(int)
    wall_masks = defaultdict(int)
    directions = [0]*4
    if force == 0:
        return clear_masks,wall_masks,tuple(directions),0
    trim_limit = min(60,len(hull)//12)
    for cell in sorted(hull):
        for r,direction in enumerate(DIR):
            front = move(cell,direction)
            service = move(front,direction)
            if front not in hull or service not in hull:
                continue
            x,z = min(cell[0],front[0]),min(cell[1],front[1])
            _,root,foot = footprint('SmallThruster',x,z,r)
            if foot != {cell,front} or foot & reserved:
                continue
            exhaust_cells = set(exhaust('SmallThruster',root,r)) & hull
            if not exhaust_cells <= trimmable or len(exhaust_cells) > min(2,trim_limit):
                continue
            bit = 1 << (sum(m.bit_count() for m in directions))
            directions[(r + (2 if force < 0 else 0)) % 4] |= bit
            for c in foot | exhaust_cells:
                clear_masks[c] |= bit
            for c in exhaust_cells | {service}:
                wall_masks[c] |= bit
    return clear_masks,wall_masks,tuple(directions),abs(force)


class MountTopology:
    """Check the same retained foundation, roof/vent graph and access cells used by the layout."""

    def __init__(self, foundation, start, air, walk, required, vents=(), reserved_band=False):
        self.foundation = foundation
        self.start = start
        self.air = air
        self.walk = walk
        self.required = required
        self.reserved_band = reserved_band
        if reserved_band:
            self.base_foundation = flood(start,foundation)
            self.base_air = flood(min(air),air) if air else set()
            self.base_walk = flood(start,walk)
        self.unused = frozenset()
        self.max_unused_block = None
        self.thermal_rooms = []
        unassigned = air-set(vents)
        while unassigned:
            room = flood(min(unassigned),unassigned)
            self.thermal_rooms.append(room)
            unassigned -= room

    def oversized_deck(self, remaining):
        while len(remaining) > self.max_unused_block:
            block = flood(min(remaining),remaining)
            if len(block) > self.max_unused_block:
                return block
            remaining -= block
        return set()

    def conflicts(self, mounts, *, check_deck=True):
        occupied = set().union(*(m['cells'] for m in mounts))
        trimmed = set().union(*(m['trim'] for m in mounts))
        foundation = self.foundation-trimmed
        # For any separated component, opening its cut frontier reconnects it,
        # or it must be removed entirely. A surviving witness makes a valid cut.
        connected = flood(self.start,foundation) if trimmed or not self.reserved_band else self.base_foundation
        if foundation-connected:
            witness = min(foundation-connected)
            component = flood(witness,foundation-connected)
            return ('foundation',witness,adjacent(component) & trimmed,None)
        air = self.air-occupied
        if air:
            connected = (flood(min(air),air) if self.air & occupied or not self.reserved_band
                         else self.base_air)
            if air-connected:
                witness = min(air-connected)
                component = flood(witness,air-connected)
                # This component can also become the only surviving air space
                # if the entire other side disappears.
                other = min(connected)
                return ('air',witness,adjacent(component) & self.air & occupied,other)
        for room in self.thermal_rooms:
            if not room & occupied:
                continue
            surviving = room-occupied
            if not surviving:
                continue
            connected = flood(min(surviving),surviving)
            if surviving-connected:
                witness = min(surviving-connected)
                component = flood(witness,surviving-connected)
                return ('heat',witness,adjacent(component) & room & occupied,min(connected))
        walk = (flood(self.start,self.walk-occupied) if self.walk & occupied or not self.reserved_band
                else self.base_walk)
        frontier = adjacent(walk) & self.walk & occupied
        if missing := self.required-walk:
            witness = min(missing)
            return ('access',None,{witness} if witness in occupied else frontier,None)
        for i,mount in enumerate(mounts):
            if not mount['service'] & walk:
                return ('maintenance',None,frontier,i)
        if check_deck and self.max_unused_block is not None:
            if block := self.oversized_deck(self.unused - occupied - trimmed):
                return ('deck',None,block,None)
        return None


def mass_case(report, records, stacks):
    points = [(x+.5,z+.5,1) for x,z in report['foundation']]
    intrinsic = 0
    for record in records:
        if record['category'] != 'Building' or record['layer'] == 'marker':
            continue
        a,b,c,d = bounds(record['cells'])
        m = record['mass'] + (37.5*stacks if record['name'] == 'DeltaV_CargoStorage' else 0)
        points.append(((a+c+1)/2,(b+d+1)/2,m))
        intrinsic += m*((c-a+1)**2 + (d-b+1)**2)/12
    mass = sum(m for _,_,m in points)
    cx = sum(x*m for x,_,m in points)/mass
    cz = sum(z*m for _,z,m in points)/mass
    inertia = intrinsic + sum(m*((x-cx)**2+(z-cz)**2) for x,z,m in points)
    radius = max(math.hypot(x+.5-cx,z+.5-cz) for x,z in report['foundation'])
    return mass,cx,cz,inertia,radius


def mount_proposals(geometry, defs, parents, terrains, terrain_parents, hull, walls, walkable, port, port_rot,
                    inset=frozenset()):
    prop, _, footprint, _, exhaust = geometry
    approaches = {move(port,DIR[port_rot],n) for n in range(5)}
    support = material_cost(terrains['Substructure'],terrain_parents) + material_cost(terrains['MetalTile'],terrain_parents)
    support += material_cost(defs['HiddenConduit'],parents)
    wall_cost = material_cost(defs['GravshipHull'],parents)
    wall_cost['Steel'] += int(prop('GravshipHull','costStuffCount','0'))
    mounts = []
    for kind in ('LargeThruster','SmallThruster'):
        body_cost = material_cost(defs[kind],parents)
        force = thruster_force(prop, kind)
        if force == 0:
            continue
        for r,direction in enumerate(DIR):
            for x,z in sorted(walls):
                for recessed in ((False,True) if inset else (False,)):
                    p = move((x,z),direction,1 if recessed else -1)
                    origin = min(p[0],x),min(p[1],z)
                    _,root,cells = footprint(kind,*origin,r)
                    face = {c for c in cells if move(c,direction) not in cells}
                    rear = cells-face
                    removed, outside = (rear,set()) if recessed else (face,rear)
                    if (not removed <= walls or cells & approaches or
                        (not face <= inset if recessed else cells & hull != face)):
                        continue
                    service = {move(c,direction) for c in face} & walkable
                    if not service:
                        continue
                    exclusion = frozenset(exhaust(kind,root,r))
                    if exclusion & (hull | approaches):
                        continue
                    a,b,c,d = bounds(cells)
                    cost = Counter(body_cost)
                    for _ in outside: cost.update(support)
                    for _ in removed: cost.subtract(wall_cost)
                    weight = float(prop(kind,'statBases/Mass','4'))
                    wall_weight = float(prop('GravshipHull','statBases/Mass','4'))
                    cable_weight = float(prop('HiddenConduit','statBases/Mass','4'))
                    mass = weight + len(outside)*(1+cable_weight) - len(removed)*wall_weight
                    mounts.append(dict(defname=kind,origin=origin,rotation=r,cells=frozenset(cells),face=frozenset(face),
                                       exclusion=exclusion,extension=frozenset(outside),cost=cost,mass=mass,
                                       service=frozenset(service),force=force,
                                       center=((a+c+1)/2,(b+d+1)/2)))
    for mount in mounts:
        mount['def'] = mount.pop('defname')
    return mounts


def limits_from_native(native):
    return [[row['acceleration'] * (1 if case['stacks'] == 12 and row['axis'] in ('forward','brake') else .9)
             for row in case['axes']] for case in native]


def direction_capacity(mounts, trim_limit, budget, facing=0):
    """Independent upper bounds; excludes shared service and torque effects."""
    capacities = []
    for offset in (1,3,0,2):
        candidates = [e for e in mounts if sum(a*b for a,b in
                      zip(force_vector(e), DIR[(facing+offset)%4])) > 0]
        model = cp_model.CpModel()
        choices = [model.new_bool_var(f'm{i}') for i in range(len(candidates))]
        cells, exhaust = defaultdict(list), defaultdict(list)
        for engine,v in zip(candidates,choices):
            for cell in sorted(engine['cells']):
                cells[cell].append(v)
            for cell in sorted(engine.get('trim',())):
                exhaust[cell].append(v)
        for values in cells.values():
            model.add_at_most_one(values)
        cuts = []
        for cell,values in exhaust.items():
            cut = model.new_bool_var(f'cut{cell}')
            model.add_max_equality(cut,values)
            if cell in cells:
                model.add(sum(cells[cell])+cut <= 1)
            cuts.append(cut)
        model.add(sum(cuts) <= trim_limit)
        model.maximize(sum(round(abs(e['force'])/THRUST)*v for e,v in zip(candidates,choices)))
        solver = solver_for(0,budget)
        status = solver.solve(model)
        capacities.append(math.floor(solver.best_objective_bound+1e-6) if status in (
            cp_model.FEASIBLE,cp_model.OPTIMAL) else sum(round(abs(e['force'])/THRUST) for e in candidates))
    return capacities


def trim_power(mounts, report, records, limits, seed, budget, native=None, task=False):
    """Balance raw turn authority with authored engine limits and known final centers."""
    model = cp_model.CpModel()
    power = [model.new_int_var(20 if native else 45,100,f'power{i}') for i in range(len(mounts))]
    facing = report.get('flightRotation',0)
    vectors = [force_vector(e, facing) for e in mounts]
    cases = [mass_case(report,records,stacks) for stacks in (0,12,48)]
    if native:
        cases = [(case['mass'],*case['center'],case['inertia'],
                  max(math.hypot(x+.5-case['center'][0],z+.5-case['center'][1])
                      for x,z in report['foundation'])) for case in native]
    residuals = []
    for k,(mass,cx,cz,inertia,radius) in enumerate(cases):
        arms = [round(10*((e['center'][0]-cx)*d[1]-(e['center'][1]-cz)*d[0]))
                for e in mounts for d in (force_vector(e),)]
        for direction in (-1,1):
            raw = [i for i,a in enumerate(arms) if direction*a > 0]
            strength = sum(abs(arms[i])*power[i] for i in raw)
            angular_floor = limits[k][4 if direction > 0 else 5]
            model.add(strength >= math.ceil((1 if task else 1.025)*angular_floor*inertia*1000/THRUST))
            for axis in (0,1):
                residual = model.new_int_var(0,100*sum(abs(d[axis]) for d in vectors),f'imbalance{k}_{direction}_{axis}')
                model.add_abs_equality(residual,sum(vectors[i][axis]*power[i] for i in raw))
                # Saturated turn groups must already be close to balanced;
                # production can improve on this but isn't required to rescue it.
                model.add(round(1000*radius)*residual <= (7 if task else 6)*strength)
                residuals.append(residual)
        for axis,(coordinate,direction) in enumerate(((0,1),(0,-1),(1,1),(1,-1))):
            controls = [model.new_int_var(0,100,f'command{k}_{axis}_{i}') for i in range(len(mounts))]
            for u,p in zip(controls,power):
                model.add(u <= p)
            main = sum(u*d[coordinate]*direction for u,d in zip(controls,vectors))
            side = sum(u*d[1-coordinate] for u,d in zip(controls,vectors))
            moment = sum(u*a for u,a in zip(controls,arms))
            model.add(main >= math.ceil(limits[k][axis]*mass*100/THRUST))
            tolerance = 10 if task else 5
            model.add(100*side <= tolerance*main)
            model.add(-100*side <= tolerance*main)
            model.add(100*moment <= round(10*tolerance*radius)*main)
            model.add(-100*moment <= round(10*tolerance*radius)*main)
    model.maximize(20*sum(p*max(0,d[1]) for p,d in zip(power,vectors))
                   + 5*sum(p*round(abs(e['force'])/THRUST) for p,e in zip(power,mounts)) - 6*sum(residuals))
    solver = solver_for(seed,budget)
    status = solver.solve(model)
    return [solver.value(p) for p in power] if status in (cp_model.FEASIBLE,cp_model.OPTIMAL) else None


def mount_selections(mounts, target, hull, base, records, limits, materials, seed, budget,
                  guided, rejected=(), shift=(0.,0.), trim_limit=0, task=False,
                  topology=None, banks=(), max_unused_percent=None, angular_priority=False, progress=None,
                  attempts=1):
    if progress:
        progress('PropulsionModel',f'mounts={len(mounts)}')
    model = cp_model.CpModel()
    variables = [model.new_bool_var(f'e{i}') for i in range(len(mounts))]
    occupied = defaultdict(list)
    for i,e in enumerate(mounts):
        for cell in e['cells']:
            occupied[cell].append(i)
    for indexes in occupied.values():
        model.add_at_most_one([variables[i] for i in indexes])
    for rotation,faces in banks:
        use = [v for e,v in zip(mounts,variables) if e['rotation'] == rotation and e['face'] <= faces]
        model.add(sum(use) >= max(2,len(faces)//2))
    if trim_limit:
        for i,e in enumerate(mounts):
            if any(c not in occupied for c in e['service']):
                continue
            unblocked = []
            for c in sorted(e['service']):
                v = model.new_bool_var(f'maintenance{i}_{c}')
                model.add(v + sum(variables[j] for j in occupied[c]) == 1)
                unblocked.append(v)
            model.add(variables[i] <= sum(unblocked))
    conflicts = {tuple(sorted((i,j))) for i,e in enumerate(mounts) for cell in e['exclusion']
                 for j in occupied[cell] if i != j}
    for i,j in sorted(conflicts):
        model.add_at_most_one(variables[i],variables[j])
    trims = defaultdict(list)
    if trim_limit:
        for i,e in enumerate(mounts):
            for c in e.get('trim',()):
                trims[c].append(variables[i])
    cut = []
    for c,uses in sorted(trims.items()):
        v = model.new_bool_var(f'trim{c}')
        model.add_max_equality(v,uses)
        cut.append(v)
    if cut:
        model.add(sum(cut) <= trim_limit)
    if topology and max_unused_percent is not None:
        used = []
        for cell in sorted(topology.unused):
            covering = trims[cell]+[variables[i] for i in occupied[cell]]
            if covering:
                covered = model.new_bool_var(f'usedDeck{cell}')
                model.add_max_equality(covered,covering)
                used.append(covered)
        model.add(100*(len(topology.unused)-sum(used)) <=
                  max_unused_percent*(len(topology.foundation)-sum(cut)))
    if materials is not None:
        names = set(materials) if trim_limit else set(materials) | set(base['materials'])
        for material in sorted(names):
            model.add(base['materials'].get(material,0) + sum(e['cost'][material]*v for e,v in zip(mounts,variables))
                      <= materials.get(material,0))
    if guided:
        inside = len(target & hull) + sum(len(e['extension'] & target)*v for e,v in zip(mounts,variables))
        outside = len(target | hull) + sum(len(e['extension'] - target)*v for e,v in zip(mounts,variables))
        model.add(100*inside >= 65*outside)
    for selection in rejected:
        model.add_bool_or([v.Not() if i in selection else v for i,v in enumerate(variables)])
    if not variables:
        yield dict(status='NO_MOUNTS')
        return
    cases = [mass_case(base,records,stacks) for stacks in (0,12,48)]
    facing = base.get('flightRotation',0)
    vectors = [force_vector(e, facing) for e in mounts]
    target_mass = cases[1][0]
    # Pure rotation can reuse a zero-resultant vector at all centers, but each
    # translation needs its own allocation when cargo moves the center of mass.
    moments = [[round(10*((e['center'][0]-cx-shift[0])*force_vector(e)[1] -
                         (e['center'][1]-cz-shift[1])*force_vector(e)[0])) for e in mounts]
               for _,cx,cz,_,_ in cases]
    if trim_limit and not task:
        # Leave the native full-turn controller a set that can be balanced
        # with per-engine power limits across all three cargo states.
        for row in moments:
            for sign in (-1,1):
                for axis in (0,1):
                    raw = sum(force_vector(e)[axis]*v for e,v,arm in zip(mounts,variables,row) if sign*arm > 0)
                    balance = 2*max(abs(d[axis]) for d in vectors)
                    model.add(raw <= balance)
                    model.add(raw >= -balance)
    forward = None
    turn_margin = model.new_int_var(0,100,'turnMargin') if angular_priority else None
    for axis,k in ([(axis,k) for axis in range(4) for k in range(3)] + [(4,1),(5,1)]):
        u = [model.new_int_var(0,QUANTA,f'u{axis}_{k}_{i}') for i in range(len(mounts))]
        for throttle,v in zip(u,variables):
            model.add(throttle <= QUANTA*v)
        fx = sum(t*d[0] for t,d in zip(u,vectors))
        fz = sum(t*d[1] for t,d in zip(u,vectors))
        if axis < 4:
            target_axis = (fx,-fx,fz,-fz)[axis]
            secondary = fz if axis < 2 else fx
            tolerance = 10 if task else 7
            model.add(100*secondary <= tolerance*target_axis)
            model.add(-100*secondary <= tolerance*target_axis)
            if axis == 2 and k == 1:
                forward = target_axis
        elif not task:
            model.add(fx == 0)
            model.add(fz == 0)
        mass,_,_,_,radius = cases[k]
        moment = sum(t*m for t,m in zip(u,moments[k]))
        if axis < 4:
            floor = limits[k][axis]
            minimum = (math.ceil(100*floor*mass*QUANTA/THRUST)
                       + sum(round(100*floor*e['mass']*QUANTA/THRUST)*v
                             for e,v in zip(mounts,variables)))
            model.add(100*target_axis >= minimum)
            if task:
                positive = (abs(d[axis//2])*v for d,v in zip(vectors,variables)
                            if d[axis//2]*(1 if axis%2 == 0 else -1) > 0)
                model.add(100*QUANTA*sum(positive) >= minimum)
            arm_tolerance = round((100 if task else 75)*radius)
            model.add(100*moment <= arm_tolerance*target_axis)
            model.add(-100*moment <= arm_tolerance*target_axis)
        else:
            required = max(limits[j][axis]*case[3] for j,case in enumerate(cases))
            torque = moment if axis == 4 else -moment
            minimum = math.ceil((1 if task else 1.10)*required*10*QUANTA/THRUST)
            model.add(torque >= minimum)
            if turn_margin is not None:
                model.add(100*torque >= minimum*(100+turn_margin))
            if task:
                sign = 1 if axis == 4 else -1
                model.add(QUANTA*sum(max(0,sign*a)*v for a,v in zip(moments[k],variables)) >= minimum)
                # Surrogate only; the native allocator checks the Euclidean
                # residual and real final inertia across all three loads.
                for component in (fx,fz):
                    model.add(round(142*radius)*component <= torque)
                    model.add(-round(142*radius)*component <= torque)
    solver = solver_for(seed,budget)
    topology_rejections = Counter()
    topology_cuts = set()

    def add_topology_cut(conflict, selection):
        kind,witness,frontier,other = conflict
        if kind == 'maintenance':
            other = selection[other]
        key = kind,witness,frozenset(frontier),other
        if key in topology_cuts:
            return
        topology_cuts.add(key)
        topology_rejections[kind] += 1
        if kind == 'deck':
            # Every one of these cells stays connected if no selected nozzle
            # cuts or occupies any of them.
            changes = {v.Index():v for c in frontier for v in
                       trims[c]+[variables[i] for i in occupied[c]]}
            model.add_bool_or([changes[i] for i in sorted(changes)])
            return
        blockers = trims if kind == 'foundation' else occupied
        alternatives = []
        for c in sorted(frontier):
            clear = model.new_bool_var(f'clear{kind}{c}')
            uses = blockers[c]
            if kind != 'foundation':
                uses = [variables[i] for i in uses]
            model.add(sum(uses) == 0).only_enforce_if(clear)
            alternatives.append(clear)
        if kind == 'maintenance':
            alternatives.append(variables[other].Not())
            other = None
        for c in (witness,other):
            if c is not None:
                alternatives.extend(blockers[c] if kind == 'foundation' else
                                    [variables[i] for i in blockers[c]])
        model.add_bool_or(alternatives)

    if topology:
        # A single recessed nozzle can already separate a duct or block
        # a maintenance throat. Add these cuts before throttle optimization.
        for i,mount in enumerate(mounts):
            if conflict := topology.conflicts([mount],check_deck=False):
                add_topology_cut(conflict,[i])

    def solve(phase):
        remaining = budget
        while remaining > 0:
            solver.parameters.max_deterministic_time = remaining
            if progress:
                progress('Propulsion',f'phase={phase} det_budget={remaining:.3f}')
            status = solver.solve(model)
            if progress:
                progress('Propulsion',f'phase={phase} status={solver.status_name(status)} '
                         f'wall={solver.wall_time:.3f}s det={solver.response_proto.deterministic_time:.3f}')
            if not topology or status not in (cp_model.OPTIMAL,cp_model.FEASIBLE):
                return status
            selection = [i for i,v in enumerate(variables) if solver.boolean_value(v)]
            conflict = topology.conflicts([mounts[i] for i in selection])
            if not conflict:
                return status
            add_topology_cut(conflict,selection)
            remaining -= max(.001,solver.response_proto.deterministic_time)
        return cp_model.UNKNOWN
    # A ratio objective with selected engine mass. Dinkelbach iterations use a
    # fixed point scale so the main axis is acceleration, not engine count.
    loaded_mass = round(10*target_mass) + sum(round(10*e['mass'])*v for e,v in zip(mounts,variables))
    solver.parameters.linearization_level = 2
    factor = round(100000*limits[1][2]*QUANTA/(10*THRUST))
    best = None
    objective_bound = None
    if task:
        # Keep a topology-safe feasible set even if optimizing acceleration
        # spends the remaining budget proving a bound.
        status = solve('feasibility')
        if status in (cp_model.OPTIMAL,cp_model.FEASIBLE):
            best = (solver.value(forward),solver.value(loaded_mass),
                    tuple(i for i,v in enumerate(variables) if solver.boolean_value(v)), 'FEASIBLE')
            for i,v in enumerate(variables):
                model.add_hint(v,i in best[2])
    for _ in range(0 if angular_priority and best is not None else 2):
        model.maximize(100000*forward - factor*loaded_mass)
        status = solve('acceleration')
        if status not in (cp_model.OPTIMAL,cp_model.FEASIBLE):
            break
        f,m = solver.value(forward),solver.value(loaded_mass)
        if best is None or f*best[1] >= best[0]*m:
            best = (f,m,tuple(i for i,v in enumerate(variables) if solver.boolean_value(v)),solver.status_name(status))
        # Bound the surrogate acceleration using its fractional-objective residual.
        min_mass = max(1, round(10*target_mass) + sum(min(0,round(10*e['mass'])) for e in mounts))
        upper = (factor + max(0,solver.best_objective_bound)/min_mass) / 100000 * 10 * THRUST/QUANTA
        objective_bound = min(objective_bound,upper) if objective_bound is not None else upper
        updated = round(100000*f/m)
        if updated == factor: break
        factor = updated
    if best is None:
        yield dict(status=solver.status_name(status),topologyRejections=dict(topology_rejections))
        return
    f,m,selection,status_name = best
    if not angular_priority:
        model.add(100*m*forward >= 95*f*loaded_mass)
    # Left/right mirror transform is on physical occupied cells, so even-sized
    # mounts mirror correctly without assuming their integer roots match.
    x0,z0,x1,z1 = bounds(target)
    positions = {(e['def'],e['rotation'],e['cells']):i for i,e in enumerate(mounts)}
    singles, pairs = [], []
    for i,e in enumerate(mounts):
        mirror = frozenset((x0+x1-x,z) if facing % 2 == 0 else (x,z0+z1-z) for x,z in e['cells'])
        j = positions.get((e['def'],(2*facing-e['rotation'])%4,mirror))
        if j is None:
            singles.append(variables[i])
        elif i < j:
            diff = model.new_bool_var(f'unpaired{i}_{j}')
            model.add_abs_equality(diff,variables[i]-variables[j])
            pairs.append(diff)
    bump = [len(e['extension'])*2+len(e['extension']-target)+4*len(e.get('trim',())) for e in mounts]
    raw_imbalance = []
    # The production allocator's full-turn target starts at raw directional
    # authority. Prefer layouts that reach that authority without a side kick.
    for k in (0,1,2):
        for sign in (-1,1):
            for axis in (0,1):
                residual = model.new_int_var(0,sum(abs(force_vector(e)[axis]) for e in mounts),f'raw{k}_{sign}_{axis}')
                model.add_abs_equality(residual,sum(force_vector(e)[axis]*v
                    for i,(e,v) in enumerate(zip(mounts,variables)) if sign*moments[k][i] > 0))
                raw_imbalance.append(residual)
    preference_scale = max(1,max(round(abs(e['force'])/THRUST) for e in mounts))
    preference = (70*sum(raw_imbalance) + preference_scale*
                  (30*sum(singles+pairs) + sum(b*v for b,v in zip(bump,variables))))
    if turn_margin is not None:
        preference += preference_scale*(2*sum(variables)-35*turn_margin)
    model.minimize(preference)
    status = solve('placement')
    if status in (cp_model.OPTIMAL,cp_model.FEASIBLE):
        selection = tuple(i for i,v in enumerate(variables) if solver.boolean_value(v))
    for attempt in range(attempts):
        yield dict(status=status_name,indexes=selection,bestSurrogate=f/m*10*THRUST/QUANTA,
                   surrogateBound=objective_bound,appearanceStatus=solver.status_name(status),
                   topologyRejections=dict(topology_rejections))
        if attempt+1 == attempts:
            break
        topology_rejections.clear()
        excluded = set(selection)
        model.add_bool_or([v.Not() if i in excluded else v for i,v in enumerate(variables)])
        model.clear_hints()
        status = solve('placement_retry')
        if status not in (cp_model.OPTIMAL,cp_model.FEASIBLE):
            yield dict(status=solver.status_name(status),topologyRejections=dict(topology_rejections))
            return
        selection = tuple(i for i,v in enumerate(variables) if solver.boolean_value(v))
        f,m = solver.value(forward),solver.value(loaded_mass)
