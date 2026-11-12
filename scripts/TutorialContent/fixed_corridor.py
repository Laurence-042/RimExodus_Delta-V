"""Freeze a four-connected corridor before any rooms are selected."""
from collections import deque
from functools import lru_cache

from module_layout import adjacent, flood
from ship_search import DIR, Route, bounds, elongated_hull, move, room_option_geometry, room_slots


@lru_cache(maxsize=1)
def room_sections():
    # A usable depth includes the outside wall and the room's entry wall.
    return sorted({(max(z for _,z in cells)+1,max(x for x,_ in cells)+1)
                   for name in ('ModuleCargo','ModuleMachining','ModuleBedroom')
                   for r,_,cells,*_ in room_slots(name) if r == 0})


def transverse_depth(center, direction, hull):
    depth = 0
    while move(center,direction,depth+1) in hull:
        depth += 1
    return depth


def section_width(depths, widths, sections, span=1):
    """Pick the widest useful corridor that preserves two real room sections."""
    def supports(width):
        return any(all(any(a >= depth and span >= frontage for depth,frontage in sections)
                       for a in (depths[0]-offset,depths[1]-(width-1-offset)))
                   for offset in ((1,) if width == 3 else range(width)))
    for w in (3,2,1):
        if w in widths and supports(w):
            return w
    # No pair of rooms fits this section. Use the least area for transit.
    return min(widths)


def straight_runs(paths):
    for path in paths:
        start = 0
        while start+1 < len(path):
            dx = path[start+1][0]-path[start][0]
            dz = path[start+1][1]-path[start][1]
            end = start+1
            while end+1 < len(path) and move(path[end],(dx,dz)) == path[end+1]:
                end += 1
            yield path[start:end+1], (abs(dz),abs(dx))
            start = end


def port_connection(start, inner, axis):
    previous = {start:None}
    queue = deque([start])
    while queue:
        cell = queue.popleft()
        if cell in axis:
            path = []
            while cell is not None:
                path.append(cell)
                cell = previous[cell]
            return path
        for neighbor in sorted((adjacent({cell}) & inner)-previous.keys()):
            previous[neighbor] = cell
            queue.append(neighbor)
    return ()


def freeze_corridor(target, graph, dock, widths, reserved=frozenset(), large_rooms=()):
    port,rotation = dock
    outside = {move(port,DIR[rotation],i) for i in range(1,5)}
    usable = target-reserved
    inner = {c for c in usable-{port} if adjacent({c}) <= usable and c not in outside}
    axis = set(graph.axis)
    if not axis or not axis <= inner:
        return None
    start = move(port,DIR[rotation],-1)
    if start not in inner or not (approach := port_connection(start,inner,axis)):
        return None
    axis.update(approach)
    sections = room_sections()
    preferred, profiles = {}, {}
    for run,normal in straight_runs(graph.paths):
        negative = (-normal[0],-normal[1])
        depths = [tuple(transverse_depth(c,d,usable) for d in (negative,normal)) for c in run]
        for i,c in enumerate(run):
            # Require continuous space over a real room frontage, not a
            # momentary widening of a single row of foundation.
            radius = min(3,(len(run)-1)//2)
            low = max(0,min(i-radius,len(run)-(2*radius+1)))
            window = depths[low:low+2*radius+1]
            clearance = tuple(min(d[j] for d in window) for j in (0,1))
            w = section_width(clearance,widths,sections,len(run))
            preferred[c] = min(preferred.get(c,w),w)
            profiles[c] = normal,clearance,len(run)

        # A second axis is useful when one side of a long trunk has room for
        # another full row of rooms between two independently useful lanes.
        if len(run) >= 12 and 2 in widths:
            depth,frontage = min(sections)
            for direction,side in ((negative,0),(normal,1)):
                offset = depth+2
                if min(d[side] for d in depths) < offset+depth+2 or len(run)-2 < frontage:
                    continue
                branch = {move(c,direction,offset) for c in run[1:-1]}
                links = {move(run[i],direction,k) for i in (1,-2) for k in range(offset)}
                if not branch & axis and branch | links <= inner:
                    axis |= branch | links
                    preferred.update({c:2 for c in branch | links})
                    break

    def lay(centers, blocked):
        floors, rows = set(), []
        for c in sorted(centers):
            width = preferred.get(c,min(widths))
            alternatives = [w for w in (width,2,1,3) if w in widths and w <= width]
            for w in dict.fromkeys(alternatives):
                offsets = [(a,b) for a in range(w) for b in range(w)]
                options = [(a,b,{(c[0]+dx-a,c[1]+dz-b) for dx in range(w) for dz in range(w)})
                           for a,b in offsets]
                allowed = [(a,b,foot) for a,b,foot in options if foot <= inner and not foot & blocked]
                if allowed:
                    if c in profiles:
                        normal,depths,span = profiles[c]
                        def fits_room(option):
                            offset = option[0] if normal[0] else option[1]
                            return all(any(a >= depth and span >= frontage for depth,frontage in sections)
                                       for a in (depths[0]-offset,depths[1]-(w-1-offset)))
                    else:
                        def fits_room(option):
                            return False
                    a,b,foot = max(allowed,key=lambda p:(fits_room(p),int(w != 3 or p[:2] == (1,1)),
                                                       len(floors & p[2]),-p[0],-p[1]))
                    floors.update(foot)
                    rows.append((c,w,a,b))
                    break
            else:
                return None
        return floors,rows

    if not (laid := lay(axis,set())):
        return None
    floors,rows = laid
    room_clearance = set()
    # The core junctions never move. A large end room can set the end of a
    # terminal branch and the position of its two-cell entrance apron.
    junctions = {c for c in axis if len(adjacent({c}) & axis) >= 3} | set(approach)
    terminals = {c for c in graph.axis if len(adjacent({c}) & graph.axis) == 1}
    for index,(name,anchor) in enumerate(large_rooms):
        options = room_option_geometry(name,frozenset(usable),anchor,True)
        options = [p for p in options if not p.cells & (room_clearance | outside | {port})]
        if name == 'ModuleBridge' and elongated_hull(target):
            x0,z0,x1,z1 = bounds(target)
            heading = int(z1-z0 > x1-x0)
            for p in options:
                if p.rot%2 != heading or p.cells & axis or not p.approaches <= axis:
                    continue
                if laid := lay(axis,room_clearance | p.cells):
                    floors,rows = laid
                    room_clearance.update(p.cells)
                    junctions.update(p.approaches)
                    break
            else:
                p = None
            if p is not None:
                continue
        fitting = [p for p in options if p.approaches <= floors and not p.cells & floors]
        if fitting:
            room_clearance.update(fitting[0].cells)
            junctions.update(fitting[0].approaches & axis)
            continue
        ranked = []
        for p in options:
            removed = p.cells & axis
            if not removed or removed & junctions or not removed & terminals:
                continue
            centers = (axis-removed) | p.approaches
            if not centers <= inner or flood(start,centers) != centers:
                continue
            ranked.append((len(removed),p.x,p.z,p.rot,p,centers))
        for *_,p,centers in sorted(ranked,key=lambda r:r[:4]):
            if laid := lay(centers,room_clearance | p.cells):
                axis = centers
                floors,rows = laid
                room_clearance.update(p.cells)
                junctions.update(p.approaches)
                break
        else:
            if index < len(large_rooms)-1 or name != 'ModuleFuel' or anchor is not None:
                return None
    if flood(start,axis) != axis:
        return None
    degrees = [len(adjacent({c}) & axis) for c in axis]
    topology = 'Cross' if 4 in degrees else 'Junction' if 3 in degrees else 'Spine'
    return Route(topology,frozenset(axis),frozenset(floors),port,rotation,max(r[1] for r in rows),
                 frozenset(floors),frozenset(axis),tuple(rows))
