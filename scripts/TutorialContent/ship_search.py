"""WARGEAR-style proposal generation and CP-SAT packing for pressure modules."""
from collections import Counter, defaultdict
from dataclasses import dataclass, replace
from functools import lru_cache, reduce
from heapq import heapify, heappop, heappush
import math
from operator import or_
from pathlib import Path
import random

from ortools.sat.python import cp_model

from module_layout import adjacent, compartment, flood, shape


DIR = ((0, 1), (1, 0), (0, -1), (-1, 0))
GALLEY = compartment(7, 8, [
    ('ElectricStove', 1, 4, 2), ('Table2x2c', 4, 4), ('DiningChair', 4, 6, 2),
    ('DiningChair', 5, 6, 2), ('ShelfSmall', 1, 6, 0, {'allowedDefs': ['MealSurvivalPack']}),
])
MANDATORY = ('ModuleBridge', 'ModuleCargo', 'ModuleCargo', 'ModuleBedroom',
             'ModuleFuel', 'ModuleGenerator', 'Galley')
ROLES = ('Transport', 'Machining', 'Fabrication')
REFUEL = "comps/li[@Class='CompProperties_Refuelable']/"


def manifest(seed, weights):
    rng = random.Random(seed)
    role = rng.choices(ROLES, weights)[0]
    extras = [] if role == 'Transport' else ['Module' + role]
    if role == 'Fabrication' and rng.random() < .45:
        extras.append('ModuleResearch')
    return role, MANDATORY + tuple(extras)


def read_blueprint(path):
    lines = Path(path).read_text(encoding='utf-8-sig').splitlines()
    if not lines or lines[0] not in ('BTD_GRAVSHIP_V1','BTD_GRAVSHIP_V2'):
        raise ValueError(f'{path}: expected BTD_GRAVSHIP_V1 or V2')
    foundations, pilot = set(), []
    for row in lines[lines.index('TILES_START') + 1:]:
        if row == 'TILES_END':
            break
        xyz, *parts = row.split('|')
        x, _, z = map(int, xyz.split(','))
        if any(p.startswith('f:') for p in parts):
            foundations.add((x, z))
        for part in parts:
            if not part.startswith('b:'):
                continue
            for raw in part[2:].split(';'):
                name, *fields = raw.split(',')
                if name in ('PilotConsole', 'VGE_PilotCockpit'):
                    pilot.append(int(fields[1]) if len(fields) >= 2 else 0)
    if not foundations or len(set(pilot)) != 1 or not 0 <= pilot[0] < 4:
        raise ValueError(f'{path}: missing foundations or ambiguous pilot orientation')
    r = pilot[0]
    return {((x, z), (-z, x), (-x, -z), (z, -x))[r] for x, z in foundations}


def bounds(cells):
    x, z = zip(*cells)
    return min(x), min(z), max(x), max(z)


def elongated_hull(cells):
    x0,z0,x1,z1 = bounds(cells)
    spans = x1-x0+1,z1-z0+1
    return max(spans) >= 3*min(spans)


def target_mask(raw, area):
    a, b, c, d = bounds(raw)
    scale = math.sqrt(area / len(raw))
    w, h = round((c - a + 1) * scale), round((d - b + 1) * scale)
    # Inverse sampling preserves concavities instead of taking a convex hull.
    return {(x + 7, z + 7) for x in range(w) for z in range(h)
            if (a + min(c-a, int((x + .5) / scale)), b + min(d-b, int((z + .5) / scale))) in raw}


def translate(cells, x, z):
    return frozenset((a+x, b+z) for a, b in cells)


def turn(cells, w, h, r):
    return frozenset(((x,z), (z,w-1-x), (w-1-x,h-1-z), (h-1-z,x))[r] for x,z in cells)


def move(c, d, k=1):
    return c[0] + d[0]*k, c[1] + d[1]*k


@dataclass(frozen=True)
class Placement:
    module: str
    x: int
    z: int
    rot: int
    cells: frozenset
    wall: frozenset
    doors: frozenset
    vents: frozenset
    approaches: frozenset
    score: int
    cut: frozenset = frozenset()
    join: tuple = ()


@dataclass(frozen=True)
class Route:
    topology: str
    spine: frozenset
    floor: frozenset
    port: tuple
    rotation: int
    width: int = 3
    lane: frozenset = frozenset()
    clear: frozenset = frozenset()
    sections: tuple = ()


def docking_sites(target, reserved):
    for c in sorted(target-reserved):
        for rotation,direction in enumerate(DIR):
            inside = move(c,direction,-1)
            if inside in target and adjacent({inside}) <= target and all(
                    move(c,direction,k) not in target for k in range(1,5)):
                yield c,rotation


@lru_cache(maxsize=128)
def room_option_geometry(name, target, anchor, rotate_bridge=False):
    """Room proposals from the hull and room's own doors, before any route exists."""
    a,b,c,d = bounds(target)
    inner = {p for p in target if adjacent({p}) <= target}
    # Sample across spatial buckets so a large cabin cannot exhaust the list
    # with small shifts of a single position.
    geometry = []
    for r,south,cells,doors,vents,plain,interior,exterior,landings in room_slots(name,rotate_bridge):
        w,h = max(x for x,_ in cells),max(z for _,z in cells)
        origins = ((anchor[0]-x,anchor[1]-z) for x,z in sorted(interior)) if anchor else (
            (x,z) for x in range(a,c-w+1) for z in range(b,d-h+1))
        for x,z in origins:
            approaches = translate(landings,x,z)
            if not approaches <= inner:
                continue
            moved = translate(cells,x,z)
            if not moved <= target or translate(exterior,x,z) & target:
                continue
            # Prefer useful outer mounting faces and entrances looking into
            # available space, but vary the objective across deterministic restarts.
            face = len(moved-inner)
            depth = sum(move(p,south,k) in target for p in approaches for k in range(1,6))
            score = 2*min(face,10) + depth
            p = Placement(name,x,z,r,moved,translate(plain,x,z),translate(doors,x,z),
                          translate(vents,x,z),approaches,score)
            geometry.append(p)
    return tuple(geometry)


def room_options(name, target, anchor, seed, limit=60, skeleton=None, region=None, guided=False,
                 rotate_bridge=False):
    a,b,_,_ = bounds(target)
    rng = random.Random(seed)
    groups = defaultdict(list)
    for raw in room_option_geometry(name,frozenset(target),anchor,rotate_bridge):
        region_penalty = 0
        if skeleton:
            interior = raw.cells-raw.wall
            overlap = sum(skeleton.regions[c] == region for c in interior)
            if 4*overlap < len(interior):
                continue
            if skeleton.region(interior) != region:
                # Keep a region's assigned rooms near its service path while
                # allowing local repair across a shared service boundary.
                region_penalty = 14
        score = raw.score+rng.randrange(13)
        score -= region_penalty
        if guided:
            # Sparse correlation of the module access mask with the skeleton
            # distance field; keep solid footprints beside the intended lane.
            score -= 3*sum(min(6,skeleton.distance[c]) for c in raw.approaches)
            score -= 3*len((raw.cells-raw.wall) & skeleton.axis)
        p = replace(raw,score=score)
        x,z,r = p.x,p.z,p.rot
        groups[(r,(x-a)//5,(z-b)//5)].append(p)
    if anchor:
        return [p for group in groups.values() for p in group]
    for group in groups.values():
        group.sort(key=lambda p:(-p.score,p.x,p.z))
    ordered = sorted(groups.values(),key=lambda g:(-g[0].score,g[0].rot,g[0].x,g[0].z))
    return [p for i in range(3) for group in ordered if i < len(group) for p in (group[i],)][:limit]


def routing_grid(target, port, width, seed, deck_reserved=frozenset()):
    usable = target-deck_reserved
    available = {c for c in usable-{port} if adjacent({c}) <= usable}
    low = -1 if width == 3 else 0
    offsets = tuple((i,j) for i in range(low,low+width) for j in range(low,low+width))
    squares = {c:f for c in sorted(available) if (f:=translate(offsets,*c)) <= available}
    rng = random.Random(seed)
    costs = {c:10+rng.randrange(7) for c in squares}
    neighbors = {c:[move(c,d) for d in DIR if move(c,d) in squares] for c in squares}
    return offsets,squares,costs,neighbors


def demand_route(target, placements, port, rotation, width, seed, grid=None, previous=None, room_for_growth=False):
    """Grow a connected route over legal width footprints from the required landings."""
    occupied = set().union(*(p.cells for p in placements))
    start = move(port,DIR[rotation],-1)
    required = {start} | set().union(*(p.approaches for p in placements))
    offsets,base,costs,neighbors = grid or routing_grid(target,port,width,seed)
    squares = {c:f for c,f in base.items() if not f & occupied}
    terminal = {t:{move(t,o,-1) for o in offsets if move(t,o,-1) in squares} for t in required}
    if any(not choices for choices in terminal.values()):
        return None
    # Multi-source shortest paths add the cheapest still-unserved demand to
    # the network. Reusing a segment is free; there is no topology template.
    nodes = set(previous.spine) if previous else {min(terminal[start],key=lambda c:(costs[c],c))}
    if not nodes <= squares.keys():
        return None
    if room_for_growth:
        # A corridor can still reach every current door after a room blocks an
        # entire wing. Account for those lost future door landings while packing.
        connected = set(nodes)
        frontier = list(connected)
        for c in frontier:
            for n in neighbors[c]:
                if n in squares and n not in connected:
                    connected.add(n)
                    frontier.append(n)
        isolated = len(squares)-len(connected)
    while pending := {t for t in required if not terminal[t] & nodes}:
        goals = set().union(*(terminal[t] for t in pending))
        prev, dist = {}, dict.fromkeys(nodes,0)
        queue = [(0,c) for c in sorted(nodes)]
        while queue:
            value,c = heappop(queue)
            if value != dist[c]:
                continue
            if c in goals:
                while c not in nodes:
                    nodes.add(c)
                    c = prev[c]
                break
            for n in neighbors[c]:
                if n not in squares:
                    continue
                cost = value+costs[n]
                if cost < dist.get(n,math.inf):
                    prev[n],dist[n] = c,cost
                    heappush(queue,(cost,n))
        else:
            return None
    floor = set().union(*(squares[c] for c in nodes))
    clear = set(nodes)
    for t in sorted(required):
        c = min(terminal[t] & nodes,key=lambda n:(abs(t[0]-n[0])+abs(t[1]-n[1]),n))
        clear.add(t)
        clear.add((c[0],t[1]))
    assert clear <= floor and flood(start,clear) == clear
    degrees = {len(adjacent({c}) & nodes) for c in nodes}
    topology = ('Cross' if 4 in degrees else 'Junction' if 3 in degrees else
                'Spine' if len({x for x,_ in nodes}) == 1 or len({z for _,z in nodes}) == 1 else 'Bends')
    result = Route(topology,frozenset(nodes),frozenset(floor),port,rotation,width,
                   frozenset(floor),frozenset(clear))
    return (result,isolated) if room_for_growth else result


def regional_grid(target,port,widths,seed,reserved,skeleton,width_plan,guided):
    inner = {c for c in target-reserved-{port} if adjacent({c}) <= target-reserved}
    distance = skeleton.distance
    rng = random.Random(seed)
    squares, costs, landings = {}, {}, defaultdict(set)
    for c in sorted(inner):
        x,z = c
        prefer = width_plan.get(skeleton.regions[c]) or min(widths)
        for w in widths:
            # Allow the through lane at either side of a wide segment.
            # A state always has a connected square at a turn or width change.
            offsets = (1,) if w == 3 else range(w)
            for a in offsets:
                for b in offsets:
                    foot = frozenset((x+dx-a,z+dz-b) for dx in range(w) for dz in range(w))
                    if not foot <= inner:
                        continue
                    state = (c,w,a,b)
                    squares[state] = foot
                    costs[state] = 12+6*abs(w-prefer)+rng.randrange(3)
                    if guided:
                        costs[state] += 3*distance[c]
                    for t in foot:
                        landings[t].add(state)
    neighbors = {}
    for s,f in squares.items():
        c,w,a,b = s
        steps = [(move(c,d),w,a,b) for d in DIR]
        neighbors[s] = [(t,costs[t]) for t in steps if t in squares]
        neighbors[s].extend((t,10+2*abs(t[1]-w)) for t in sorted(landings[c])
                            if t[0] == c and t != s and (squares[t] <= f or f <= squares[t]))
    return squares,landings,neighbors,costs


def regional_route(placements,port,rotation,grid,previous=None):
    """Connect only real door landings, using legal width states on each step."""
    base,landings,neighbors,costs = grid
    occupied = set().union(*(p.cells for p in placements))
    start = move(port,DIR[rotation],-1)
    required = {start} | set().union(*(p.approaches for p in placements))
    @lru_cache(maxsize=None)
    def legal(s):
        return s in base and occupied.isdisjoint(base[s])
    terminal = {c:{s for s in landings.get(c,()) if legal(s)} for c in required}
    if any(not states for states in terminal.values()):
        return None
    nodes = set(previous.sections) if previous else {min(terminal[start],key=lambda s:(costs[s],s))}
    if not all(legal(s) for s in nodes):
        return None
    while pending := [t for t in required if not terminal[t] & nodes]:
        goals = set().union(*(terminal[t] for t in pending))
        goal_cells = {s[0] for s in goals}
        @lru_cache(maxsize=None)
        def remaining(c):
            return 16*min(abs(c[0]-a)+abs(c[1]-b) for a,b in goal_cells)
        prev, dist = {}, dict.fromkeys(nodes,0)
        queue = [(remaining(s[0]),0,s) for s in nodes]
        heapify(queue)
        while queue:
            _,value,s = heappop(queue)
            if value != dist[s]:
                continue
            if s in goals:
                while s not in nodes:
                    nodes.add(s)
                    s = prev[s]
                break
            for t,step in neighbors[s]:
                if not legal(t):
                    continue
                cost = value + step
                if cost < dist.get(t,math.inf):
                    prev[t],dist[t] = s,cost
                    heappush(queue,(cost+remaining(t[0]),cost,t))
        else:
            return None
    spine = {s[0] for s in nodes}
    floor = set().union(*(base[s] for s in nodes))
    clear = set(spine)
    for t in sorted(required):
        c,_,_,_ = min(terminal[t] & nodes,key=lambda s:(abs(t[0]-s[0][0])+abs(t[1]-s[0][1]),s))
        x,z = t
        clear.update((x,j) for j in range(min(z,c[1]),max(z,c[1])+1))
        clear.update((i,c[1]) for i in range(min(x,c[0]),max(x,c[0])+1))
    assert clear <= floor and flood(start,clear) == clear
    degrees = [len(adjacent({c}) & spine) for c in spine]
    topology = ('Cross' if 4 in degrees else 'Junction' if 3 in degrees else
                'Spine' if len({x for x,_ in spine}) == 1 or len({z for _,z in spine}) == 1 else 'Bends')
    return Route(topology,frozenset(spine),frozenset(floor),port,rotation,max(s[1] for s in nodes),
                 frozenset(floor),frozenset(clear),tuple(sorted(nodes)))


def grow_layout(modules, target, anchors, reserved, dock, width, seed, geometry, task=False,
                deck_reserved=frozenset(), skeleton=None, assignments=None, width_plan=None, guided=False,
                diagnostics=None, propulsion_slots=None, thrust_need=None, propulsion_weight=1):
    """Beam search places a room and immediately connects the landings it adds."""
    port,rotation = dock
    elongated = bool(skeleton and elongated_hull(target))
    end_margin = set()
    if elongated and propulsion_slots and propulsion_weight:
        x0,z0,x1,z1 = bounds(target)
        axis = 1 if z1-z0 > x1-x0 else 0
        low,high = (z0,z1) if axis else (x0,x1)
        # Keep a nozzle, its sealing face and a service strip clear at
        # each narrow end. Other proposals can use a recessed installation.
        end_margin = {c for c in target if min(c[axis]-low,high-c[axis]) < 3}
    grid = (regional_grid(target,port,width,seed,deck_reserved,skeleton,width_plan,guided) if skeleton
            else routing_grid(target,port,width,seed,deck_reserved))
    options = [room_options(module,target,anchor,seed+i,180 if task else 60,skeleton,
                            assignments[i] if skeleton else None,guided,rotate_bridge=bool(skeleton and elongated))
               for i,(module,anchor) in enumerate(zip(modules,anchors))]
    _,_,footprint,_,_ = geometry
    blocked_by_room = {}
    for i,row in enumerate(options):
        local = shape(modules[i], GALLEY if modules[i] == 'Galley' else None)
        w,h = local['size']
        blocked = local['partitions'] | set().union(*(footprint(f[0],f[1],f[2],f[3] if len(f)>3 else 0)[2]
                                                     for f in local['fixtures']))
        options[i] = [p for p in row if port not in p.cells and not (deck_reserved | end_margin) & p.cells
                      and not reserved & (p.wall | p.doors | p.vents)
                      and (not reserved & p.cells or not reserved & translate(turn(blocked,w,h,p.rot),p.x,p.z))]
        if propulsion_slots:
            clear_masks,wall_masks,_ = propulsion_slots
            for p in options[i]:
                solid = translate(turn(blocked,w,h,p.rot),p.x,p.z) | p.doors | p.vents
                blocked_by_room[p] = (reduce(or_,(clear_masks.get(c,0) | wall_masks.get(c,0) for c in solid),0)
                                      | reduce(or_,(wall_masks.get(c,0) for c in p.wall),0))
    order = sorted(range(len(modules)),key=lambda i:(i != 0,anchors[i] is None,
        ((len(options[i]) // 20, -len(options[i][0].cells)) if skeleton else
         (-len(options[i][0].cells),len(options[i]))) if task and options[i] else (0,len(options[i]))))
    initial = (regional_route((),port,rotation,grid) if skeleton else
               demand_route(target,(),port,rotation,width,seed,grid))
    if not initial:
        return
    def wall_faces(p):
        inside = p.cells-p.wall
        return {(c,d) for c in p.wall for d in DIR
                if move(c,d,-1) in inside and move(c,d) not in p.cells}
    faces = {p:wall_faces(p) for row in options for p in row} if task else {}
    exposed_cells = {c for c in target if not adjacent({c}) <= target}
    # A wall one cell inboard leaves room for the nozzle on the retained
    # foundation. Flush walls often require a recessed mount through furniture.
    fore_aft_faces = {p:sum(d[1] != 0 and move(c,d) in target and move(c,d,2) not in target
                           for c,d in face) for p,face in faces.items()}
    region = skeleton.regions if skeleton else {c:(c[0]//10,c[1]//10) for c in target}
    regions = Counter(region.values())
    room_regions = {p:Counter(region[c] for c in p.cells) for p in faces}
    traversable = {c for c in target-deck_reserved if adjacent({c}) <= target-deck_reserved} if skeleton else set()
    beam = [(0,(),initial)]
    for step,i in enumerate(order):
        next_beam = []
        remaining = order[step+1:]
        for score,placed,route in beam:
            footprint_cells = set().union(*(p.cells for p in placed))
            interiors = set().union(*(p.cells-p.wall for p in placed))
            all_landings = set().union(*(p.approaches for p in placed))
            double_walls = {(move(c,d),(-d[0],-d[1])) for q in placed for c,d in faces[q]} if task else set()
            used = route.floor | adjacent(route.floor) | footprint_cells if task else set()
            used_regions = Counter(region[c] for c in used if c in region) if task else {}
            eligible = []
            if propulsion_slots:
                used_slots = reduce(or_,(blocked_by_room[p] for p in placed),0)
                used_slots |= reduce(or_,(clear_masks.get(c,0) for c in route.floor),0)
                _,_,directions = propulsion_slots
            for p in options[i]:
                crosses_route = p.cells & route.floor
                if (p.cells & (interiors | all_landings) or (crosses_route and not task) or
                        (p.cells-p.wall) & footprint_cells or p.approaches & footprint_cells):
                    continue
                if task:
                    if not faces[p].isdisjoint(double_walls):
                        continue
                    proximity = min(abs(a-x)+abs(b-z) for a,b in p.approaches for x,z in route.spine)
                    function = 0
                    if p.module == 'ModuleCargo':
                        function = -abs(p.x-port[0])-abs(p.z-port[1])
                    if p.module in ('ModuleMachining','ModuleFabrication','ModuleResearch','ModuleBedroom'):
                        related = 'Galley' if p.module == 'ModuleBedroom' else 'ModuleCargo'
                        function = -min((abs(p.x-q.x)+abs(p.z-q.z) for q in placed if q.module == related),
                                        default=0)
                    facing = len(p.cells & footprint_cells)
                    exposed = len(p.cells & exposed_cells)
                    # The shell edge is useful for propulsion and preserves room
                    # in the middle for the next branch. Share walls when possible.
                    new_floor = len(p.cells-used)
                    spread = sum(2*n*(regions[k]-used_regions[k])//regions[k] for k,n in room_regions[p].items())
                    heuristic = (p.score+3*facing+exposed+6*fore_aft_faces[p]+function+new_floor+spread
                                 -2*proximity-2*len(crosses_route))
                    if propulsion_slots:
                        heading = (placed[0] if placed else p).rot
                        surviving = [((mask & ~used_slots) & ~blocked_by_room[p]).bit_count() for mask in directions]
                        needs = [thrust_need[(axis-heading)%4] for axis in range(4)]
                        shortage = sum(max(0,need-count) for need,count in zip(needs,surviving))
                        lost = sum(min(need,count)-min(need,surviving[axis]) for axis,(need,count) in
                                   enumerate(zip(needs,[(mask & ~used_slots).bit_count() for mask in directions])))
                        heuristic -= propulsion_weight*(18*shortage + 5*lost)
                        if not placed and elongated:
                            # Preserve the option to point the bridge toward
                            # a side with a longer usable mounting edge.
                            primary = (heading,(heading+2)%4)
                            heuristic += sum(surviving[a] for a in primary)
                else:
                    heuristic = 0
                eligible.append((heuristic,p))
            if task:
                eligible.sort(key=lambda item:-item[0])
                eligible = eligible[:(48 if skeleton else 16)]
            accepted_here = 0
            for rank,(heuristic,p) in enumerate(eligible):
                if skeleton and rank >= 16 and accepted_here >= 3:
                    break
                if skeleton:
                    occupied_next = footprint_cells | p.cells
                    solid_next = interiors | (p.cells-p.wall)
                    access_next = all_landings | p.approaches
                    nearby = [j for j in remaining if assignments[j] == assignments[i]]
                    future = Counter((modules[j],assignments[j],anchors[j]) for j in nearby)
                    examined = set()
                    viable = True
                    for j in nearby:
                        key = modules[j],assignments[j],anchors[j]
                        if key in examined:
                            continue
                        examined.add(key)
                        compatible = 0
                        for q in options[j]:
                            if (q.cells.isdisjoint(solid_next) and q.cells.isdisjoint(access_next)
                                    and (q.cells-q.wall).isdisjoint(occupied_next)
                                    and q.approaches.isdisjoint(occupied_next)):
                                compatible += 1
                                if compatible >= future[key]:
                                    break
                        if compatible < future[key]:
                            viable = False
                            break
                    if not viable:
                        continue
                previous = None if p.cells & route.floor else route
                candidate = (regional_route(placed+(p,),port,rotation,grid,previous) if skeleton else
                             demand_route(target,placed+(p,),port,rotation,width,seed,grid,previous,task))
                if not candidate:
                    continue
                if task and not skeleton:
                    candidate,isolated = candidate
                elif skeleton and i != order[-1]:
                    available = traversable-(footprint_cells | p.cells)
                    connected = flood(move(port,DIR[rotation],-1),available)
                    isolated = len(available)-len(connected)
                else:
                    isolated = 0
                # Trade edge mounting faces against the new walking distance.
                sharing = len(p.wall & footprint_cells)
                value = (score + (heuristic if task else 2*p.score) + sharing
                         - 3*(len(candidate.spine)-len(route.spine)) - 2*isolated)
                if propulsion_slots:
                    route_slots = reduce(or_,(clear_masks.get(c,0) for c in candidate.floor-route.floor),0)
                    value -= 12*propulsion_weight*(route_slots & ~used_slots & ~blocked_by_room[p]).bit_count()
                if skeleton and step >= len(order)*3//4:
                    unused = target-(occupied_next | candidate.floor | adjacent(candidate.floor))
                    largest = 0
                    remaining_deck = set(unused)
                    while remaining_deck:
                        block = flood(min(remaining_deck),remaining_deck)
                        largest = max(largest,len(block))
                        remaining_deck -= block
                    value -= 2*len(unused)+3*largest
                next_beam.append((value,placed+(p,),candidate))
                accepted_here += 1
        if not next_beam:
            if diagnostics is not None:
                diagnostics.update(blockedModule=modules[i],blockedRegion=assignments[i],
                                   placed=order.index(i),placementOptions=len(options[i]))
            return
        next_beam.sort(key=lambda row:-row[0])
        if task:
            beam, seen = [], set()
            for row in next_beam:
                _, layout, route = row
                signature = frozenset((p.module,p.x,p.z,p.rot) for p in layout),route.spine
                if signature in seen:
                    continue
                seen.add(signature)
                beam.append(row)
                if len(beam) == 5:
                    break
        else:
            beam = next_beam[:5]
    for _,placed,route in beam:
        yield [p for _,p in sorted(zip(order,placed))],route


def route_floor(spine, width):
    if width == 3:
        return {(x+dx,z+dz) for x,z in spine for dx in (-1,0,1) for dz in (-1,0,1)}
    lane = set(spine)
    if width == 2:
        for x,z in spine:
            if (x,z-1) in spine or (x,z+1) in spine:
                lane.add((x+1,z))
            if (x-1,z) in spine or (x+1,z) in spine:
                lane.add((x,z+1))
        lane |= {(x+1,z+1) for x,z in spine if (x+1,z) in lane and (x,z+1) in lane
                 and (x+1,z) not in spine and (x,z+1) not in spine}
    return lane


def route_proposals(target, seed, widths=None, exact=False):
    """Propose connected lane graphs in both axes, including asymmetric junctions."""
    a,b,c,d = bounds(target)
    rng = random.Random(seed)
    variants = [(kind, longitudinal, shift, junction, ratio)
                for kind in ('T', 'Cross', 'Elbow', 'H', 'Spine')
                for longitudinal in (True, False)
                for shift in (0, -2, 2)
                for junction, ratio in ((0, .79), (-4, .84), (4, .75))]
    rng.shuffle(variants)
    ranked = []
    for kind, longitudinal, shift, junction, ratio in variants:
        # Coordinates in a temporary frame where the main lane follows +v.
        u0,v0,u1,v1 = (a,b,c,d) if longitudinal else (b,a,d,c)
        u, v = (u0+u1)//2 + shift, (v0+v1)//2
        half = max(8, round((v1-v0)*ratio/2))
        lo, hi = v-half, v+half
        arm = max(4, round((u1-u0)*.33))
        cross = max(lo+3, min(hi-3, v+junction))
        if kind == 'Elbow':
            hi = cross
        spine = {(u,k) for k in range(lo, hi+1)}
        if kind in ('T','Cross','Elbow'):
            spine |= {(j,cross) for j in range(u-(arm if kind == 'Cross' else 0), u+arm+1)}
        elif kind == 'H':
            spine |= {(j,level) for j in range(u-arm,u+arm+1) for level in (cross-4,cross+4)}
        convert = (lambda p:p) if longitudinal else (lambda p:(p[1],p[0]))
        local_target = set(map(convert,target))
        for width in widths or (3,):
            center = set(spine)
            lane = route_floor(center,width)
            if exact:
                while outside := (lane | adjacent(lane)) - local_target:
                    bad_ends = {p for p in center if len(adjacent({p}) & center) <= 1 and
                                any(abs(x-p[0])+abs(z-p[1]) <= (1 if width == 1 else 3) for x,z in outside)}
                    if not bad_ends or len(center-bad_ends) < 5:
                        break
                    center -= bad_ends
                    lane = route_floor(center,width)
                if (lane | adjacent(lane)) - local_target:
                    continue
            floor = set(lane)
            if widths and not exact:
                for k in range(lo+2,hi-1,5):
                    for side in (-1,1):
                        anchor = u-side if width == 1 else u+side*2 if width == 3 else u-1 if side == -1 else u+2
                        bay = {(anchor,k),(anchor,k+1)}
                        if bay & lane or not bay <= local_target:
                            continue
                        if adjacent(bay) & lane:
                            floor |= bay
                            break
            center_line = [z for x,z in center if x == u]
            if not center_line:
                continue
            port = (u, min(center_line)-(2 if width == 3 else 1))
            if exact:
                # Extend the landing to the edge of the actual hull, including a clear exterior.
                while (port[0],port[1]-1) in local_target:
                    floor.add(port)
                    port = (port[0],port[1]-1)
                if port not in local_target:
                    continue
            route = Route(kind, frozenset(map(convert,center)), frozenset(map(convert,floor)),
                          convert(port), 2 if longitudinal else 3, width,
                          frozenset(map(convert,floor if exact else lane)) if widths else frozenset())
            if exact and (route.floor | adjacent(route.floor)) - target:
                continue
            ratio_inside = len(route.floor & target) / len(route.floor)
            ranked.append((ratio_inside, route))
    # Interleave topology families to avoid spending the whole budget on one corridor.
    grouped = defaultdict(list)
    for _, route in sorted(ranked, key=lambda row: -row[0]):
        grouped[route.topology].append(route)
    for i in range(max(map(len, grouped.values()),default=0)):
        for kind in ('T', 'Cross', 'Elbow', 'H', 'Spine'):
            if i < len(grouped[kind]):
                yield grouped[kind][i]


def proposals(name, route, target, guided, anchor=None, exact=False):
    ext = DIR[route.rotation]
    port_clearance = {move(route.port, ext, k) for k in range(5)}
    by_rotation = []
    for r,south,cells,doors,vents,plain,interior,exterior,landings in room_slots(name):
        entry = min(doors)
        rows = []
        for px, pz in sorted(route.floor):
            x, z = px-entry[0]-south[0], pz-entry[1]-south[1]
            if min(x,z) < 2 or anchor is not None and (anchor[0]-x,anchor[1]-z) not in interior:
                continue
            approaches = translate(landings,x,z)
            if not approaches <= route.floor:
                continue
            moved = translate(cells, x, z)
            if exact and (not moved <= target or translate(exterior,x,z) & target):
                continue
            if moved & (route.floor | port_clearance):
                continue
            # Binary footprint cross-correlation against a signed preference field.
            overlap = len(moved & target)
            score = 2*overlap - len(moved) if guided else 0
            rows.append(Placement(name,x,z,r,moved,translate(plain,x,z),
                                  translate(doors,x,z),translate(vents,x,z),approaches,score))
        by_rotation.extend(sorted(rows,key=lambda p:(-p.score,p.x,p.z))[:22])
    return sorted(by_rotation, key=lambda p: (-p.score, p.rot, p.x, p.z))


@lru_cache(maxsize=None)
def room_slots(name, rotate_bridge=False):
    local = shape(name, GALLEY if name == 'Galley' else None)
    w,h = local['size']
    exterior = frozenset(move(c,DIR[('north','east','south','west').index(side)],i)
                         for side,edge in local['edges'].items() if edge['kind'] == 'exterior'
                         for c in edge['cells'] for i in range(1,5))
    slots = []
    for r in ((0,) if (name == 'ModuleBridge' and not rotate_bridge) or exterior else range(4)):
        south = DIR[(2+r)%4]
        cells,doors,vents,plain,interior = [turn(c,w,h,r) for c in (
            local['cells'],local['doors'],local['vents'],local['shell']-local['doors']-local['vents'],
            local['interior'])]
        slots.append((r,south,cells,doors,vents,plain,interior,exterior,
                      frozenset(move(c,south) for c in doors | vents)))
    return tuple(slots)


def solver_for(seed, budget):
    solver = cp_model.CpSolver()
    solver.parameters.num_workers = 1
    solver.parameters.random_seed = seed & 0x7fffffff
    solver.parameters.max_deterministic_time = budget
    return solver


def add_open_seams(choices, modules, geometry):
    prop,_,footprint,_,_ = geometry
    cuts = defaultdict(list)
    for i,options in enumerate(choices):
        if modules[i] not in ('ModuleCargo','ModuleResearch','ModuleMachining','ModuleFabrication'):
            continue
        for p in options:
            local = shape(p.module)
            w,h = local['size']
            blocked = local['partitions'] | set().union(*(
                footprint(f[0],f[1],f[2],f[3] if len(f)>3 else 0)[2]
                for f in local['fixtures'] if prop(f[0],'passability','Standable') != 'Standable'))
            free = translate(turn(local['interior']-blocked,w,h,p.rot),p.x,p.z)
            for side,edge in local['edges'].items():
                if edge['kind'] != 'optional':
                    continue
                outward = (('north','east','south','west').index(side)+p.rot)%4
                direction = DIR[outward]
                cut = translate(turn(edge['cells'],w,h,p.rot),p.x,p.z)
                if cut & (p.doors | p.vents) or not cut <= p.wall:
                    continue
                outer = frozenset(move(c,direction,-1) for c in cut)
                across = frozenset(move(c,direction) for c in outer)
                cuts[(outer,across)].append((i,p,cut,free & outer))
    joins = defaultdict(list)
    for edges,options in cuts.items():
        mates = cuts.get(edges[::-1],())
        for i,p,cut,free in options:
            if not any(i != j and p.module != q.module and adjacent(free) & other_free and
                       not ((p.cells-cut) & (q.cells-other) - ((p.wall-cut) & (q.wall-other)))
                       for j,q,other,other_free in mates):
                continue
            joins[edges].append((i,len(choices[i])))
            choices[i].append(replace(p,cells=p.cells-cut,wall=p.wall-cut,cut=cut,join=(*edges,free)))
    return joins


def pack(modules, route, target, guided, seed, budget, max_area, geometry, rejected=(), anchors=None,
         reserved=frozenset(), fixed=None):
    """Select one slot per module and constrain true cell union and shared walls."""
    model = cp_model.CpModel()
    port = route.port
    route_hull = route.floor | adjacent(route.floor)
    choices = [[p] for p in fixed] if fixed is not None else [
        proposals(module,route,target,guided,anchor,anchors is not None)
        for module,anchor in zip(modules,anchors if anchors is not None else [None]*len(modules))]
    if any(not options for options in choices):
        return dict(status='NO_PORT_SLOTS', missing=sorted({module for module, options in zip(modules,choices)
                                                         if not options}))
    # A double cut is two complete equal optional boundaries removed. Register
    # each available half by its remaining inner edge and require its mate.
    prop,_,footprint,_,_ = geometry
    for i, options in enumerate(choices):
        if not reserved:
            break
        local = shape(modules[i], GALLEY if modules[i] == 'Galley' else None)
        w,h = local['size']
        blocked = local['partitions'] | set().union(*(footprint(f[0],f[1],f[2],f[3] if len(f)>3 else 0)[2]
                                                     for f in local['fixtures']))
        choices[i] = [p for p in options if not reserved & (p.wall | p.doors | p.vents) and
                      (not reserved & p.cells or not reserved & translate(turn(blocked,w,h,p.rot),p.x,p.z))]
        if not choices[i]:
            return dict(status='NO_MARKER_SLOTS', missing=[modules[i]])
    joins = add_open_seams(choices,modules,geometry)
    occupied, solid, wall_faces, variables, centers = defaultdict(list), defaultdict(list), defaultdict(list), [], []
    for i, options in enumerate(choices):
        row = [model.new_bool_var(f'p{i}_{j}') for j in range(len(options))]
        model.add_exactly_one(row)
        variables.append(row)
        for p, v in zip(options, row):
            for c in p.cells:
                occupied[c].append(v)
            for c in p.cells - p.wall:
                solid[c].append(v)
            inner = p.cells-p.wall-p.doors-p.vents
            for c in p.wall:
                for r,d in enumerate(DIR):
                    if move(c,d,-1) in inner and move(c,d) not in p.cells:
                        wall_faces[(c,r)].append(v)
        centers.append(tuple(sum((2*(p.x,p.z)[axis] + bounds(p.cells)[2+axis]-bounds(p.cells)[axis])*v
                                 for p,v in zip(options,row)) for axis in range(2)))
    for edges,options in joins.items():
        for i,j in options:
            model.add(variables[i][j] <= sum(variables[a][b] for a,b in joins.get(edges[::-1],())
                                             if i != a and modules[i] != modules[a] and
                                             adjacent(choices[i][j].join[2]) & choices[a][b].join[2]))
    for previous in rejected:
        hits = [row[options.index(p)] for p,options,row in zip(previous,choices,variables) if p in options]
        if len(hits) == len(modules):
            model.add_bool_or(v.Not() for v in hits)
    if len(modules) > 2 and modules[1] == modules[2] and (anchors is None or anchors[1] == anchors[2]):
        model.add(sum(j*v for j,v in enumerate(variables[1])) <=
                  sum(j*v for j,v in enumerate(variables[2])))
    for (c,r),here in sorted(wall_faces.items()):
        other = (move(c,DIR[r]),(r+2)%4)
        if (c,r) < other and other in wall_faces:
            model.add(sum(here)+sum(wall_faces[other]) <= 1)
    union = {}
    for c, uses in sorted(occupied.items()):
        model.add(sum(uses) <= 2)
        if solid[c]:
            # A fixture, door, vent, or interior cannot be shared with another module.
            model.add(sum(uses) + sum(solid[c]) <= 2)
        if c not in route_hull:
            union[c] = model.new_bool_var(f'h{c}')
            model.add_max_equality(union[c], uses)
    area = len(route_hull) + sum(union.values())
    model.add(area <= max_area)
    intersection = len(route_hull & target) + sum(v for c,v in union.items() if c in target)
    union_size = len(route_hull | target) + sum(v for c,v in union.items() if c not in target)
    if guided and anchors is None:
        # Bare hull threshold leaves a small margin for engine footprints in the final check.
        model.add(100*intersection >= 61*union_size)
    travel = []
    links = [(i,j,2) for i,n in enumerate(modules) for j,m in enumerate(modules)
             if (n == 'ModuleBedroom' and m == 'Galley') or (n == 'ModuleCargo' and
                 m in ('ModuleMachining','ModuleFabrication','ModuleResearch'))]
    for i,j,weight in links:
        for axis in range(2):
            distance = model.new_int_var(0, 240, f'route{i}_{j}_{axis}')
            model.add_abs_equality(distance, centers[i][axis] - centers[j][axis])
            travel.append(weight*distance)
    for i,n in enumerate(modules):
        if n != 'ModuleCargo':
            continue
        for axis in range(2):
            distance = model.new_int_var(0, 240, f'port{i}_{axis}')
            model.add_abs_equality(distance, centers[i][axis] - 2*port[axis])
            travel.append(distance)
    # At roughly equal area, signed symmetric difference drives overlap. Actual IoU
    # is used across the proposals, including engine extensions.
    model.minimize(60*(union_size-intersection) + sum(travel) if guided else 25*area + sum(travel))
    solver = solver_for(seed,budget)
    status = solver.solve(model)
    if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        return dict(status=solver.status_name(status))
    placements = [next(p for p,v in zip(options,row) if solver.boolean_value(v))
                  for options,row in zip(choices,variables)]
    return dict(status=solver.status_name(status), placements=placements,
                objective=solver.objective_value, bound=solver.best_objective_bound)
