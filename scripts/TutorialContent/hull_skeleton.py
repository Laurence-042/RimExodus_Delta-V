"""Four-neighbor corridor guidance and service regions for an exact hull."""
from collections import Counter, deque
from dataclasses import dataclass
from heapq import heappop, heappush

from module_layout import adjacent


def distances(hull, seeds):
    result = dict.fromkeys(seeds, 0)
    queue = deque(sorted(seeds))
    while queue:
        c = queue.popleft()
        for n in sorted((adjacent({c}) & hull) - result.keys()):
            result[n] = result[c] + 1
            queue.append(n)
    return result


RING = ((0,1),(1,1),(1,0),(1,-1),(0,-1),(-1,-1),(-1,0),(-1,1))


def thin(hull):
    """Thin the raster from alternating sides, then reconnect diagonal links."""
    axis = set(hull)
    while True:
        changed = False
        for side in (0, 1):
            remove = set()
            for x,z in axis:
                ring = [(x+dx,z+dz) in axis for dx,dz in RING]
                n,e,s,w = ring[::2]
                if not 2 <= sum(ring) <= 6 or sum(a and not b for a,b in zip(ring,ring[1:]+ring[:1])) != 1:
                    continue
                if not ((n and e and (s if side == 0 else w)) or
                        (s and w and (e if side == 0 else n))):
                    remove.add((x,z))
            axis -= remove
            changed |= bool(remove)
        if not changed:
            break
    if not axis:
        axis.add(min(hull))
    depth = distances(hull,{c for c in hull if not adjacent({c}) <= hull})
    result = set(axis)
    for x,z in sorted(axis):
        for dx in (-1,1):
            other = x+dx,z+1
            corners = {(x+dx,z),(x,z+1)}
            if other in axis and not corners & result and corners & hull:
                result.add(max(corners & hull,key=lambda c:(depth[c],c)))
    return result,depth


def segments(axis):
    links = {c:sorted(adjacent({c}) & axis) for c in axis}
    ends = {c for c,n in links.items() if len(n) != 2}
    seen, paths = set(), []
    for start in sorted(ends) + sorted(axis-ends):
        if not links[start]:
            paths.append((start,))
        for n in links[start]:
            edge = frozenset((start,n))
            if edge in seen:
                continue
            path = [start,n]
            seen.add(edge)
            while path[-1] not in ends:
                next_cell = next(p for p in links[path[-1]] if p != path[-2])
                edge = frozenset((path[-1],next_cell))
                if edge in seen:
                    break
                seen.add(edge)
                path.append(next_cell)
            paths.append(tuple(path))
    return paths


@dataclass
class Skeleton:
    raw: set
    axis: set
    paths: list
    regions: dict
    distance: dict
    depth: dict
    capacity: dict
    widths: dict

    def region(self, cells):
        return min(Counter(self.regions[c] for c in cells).items(),key=lambda v:(-v[1],v[0]))[0]

    def report(self):
        return dict(raw=sorted(self.raw),axis=sorted(self.axis),paths=self.paths,
                    regions=[dict(cells=sorted(c for c,k in self.regions.items() if k == i),
                                  capacity=cap,widths=self.widths[i])
                             for i,cap in self.capacity.items()])


def analyze(hull, protected=()):
    raw,depth = thin(hull)
    axis = set(raw)
    while True:
        prune = set()
        for p in segments(axis):
            if len(p) > 4:
                continue
            if len(adjacent({p[0]}) & axis) >= 3:
                p = p[::-1]
            if (len(adjacent({p[0]}) & axis) == 1 and len(adjacent({p[-1]}) & axis) >= 3
                    and all(abs(a-x)+abs(b-z) > 3 for x,z in protected for a,b in p[:-1])):
                prune.update(p[:-1])
        if not prune:
            break
        axis -= prune
    paths = segments(axis)
    # Small graph edges around junctions are useful to routing, but should
    # not become separate service regions with room capacity of their own.
    service = []
    for path in [p for p in paths if len(p) >= 5] or paths:
        # Separate room budgets along long trunks without changing the
        # skeleton itself. A single budget can leave an entire tip empty.
        count = max(1,(len(path)+12)//24) if len(path) >= 36 else 1
        for i in range(count):
            service.append(path[i*len(path)//count:(i+1)*len(path)//count])
    owner, distance, queue = {}, {}, []
    for i,path in enumerate(service):
        for c in path:
            heappush(queue,(0,i,c))
    while queue:
        d,i,c = heappop(queue)
        if c in owner:
            continue
        owner[c],distance[c] = i,d
        for n in (adjacent({c}) & hull)-owner.keys():
            heappush(queue,(d+1,i,n))
    capacity = dict(sorted(Counter(owner.values()).items()))
    inner = {c for c in hull if depth[c] >= 1}
    widths = {}
    for i in capacity:
        widths[i] = tuple(w for w in (1,2,3) if any(
            {(x+dx,z+dz) for dx in range(w) for dz in range(w)} <= inner
            for x,z in service[i] if owner[x,z] == i))
    return Skeleton(raw,axis,paths,owner,distances(hull,axis),depth,capacity,widths)
