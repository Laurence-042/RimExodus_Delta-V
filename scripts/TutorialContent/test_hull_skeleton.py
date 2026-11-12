"""Offline geometry checks for the hull skeleton and the demand router."""
from module_layout import adjacent, flood
from hull_skeleton import analyze
from fixed_corridor import freeze_corridor, section_width
from ship_search import Placement, regional_grid, regional_route, room_option_geometry
from ship_propulsion import MountTopology


def rectangle(x1,z1,x2,z2):
    return {(x,z) for x in range(x1,x2+1) for z in range(z1,z2+1)}


def main():
    h = rectangle(0,0,12,40) | rectangle(34,0,46,40) | rectangle(12,17,34,23)
    graph = analyze(h)
    assert len(graph.capacity) == 5 and graph.regions.keys() == h
    assert flood(min(graph.axis),graph.axis) == graph.axis
    junctions = [c for c in graph.axis if len(adjacent({c}) & graph.axis) == 3]
    assert len(junctions) == 2 and all(17 <= z <= 23 for _,z in junctions)
    straight = analyze(rectangle(0,0,12,90))
    assert len(straight.paths) == 1 and len({x for x,_ in straight.axis}) == 1
    assert len(straight.capacity) >= 3 and max(straight.capacity.values()) < len(straight.regions)//2
    widths = (1,2,3)
    assert [section_width(depths,widths,[(6,7)],9) for depths in ((6,6),(6,7),(7,7))] == [1,2,3]
    frozen = freeze_corridor(h,graph,((23,17),2),widths)
    assert frozen and graph.axis <= frozen.spine and flood((23,18),frozen.clear) == frozen.clear
    assert freeze_corridor(h,graph,((23,17),2),widths) == frozen
    narrow_wings = rectangle(7,7,19,55) | rectangle(31,7,43,55) | rectangle(19,19,31,31)
    wing_graph = analyze(narrow_wings)
    large_rooms = [('ModuleBridge',None),('Galley',None),('ModuleFuel',None)]
    frozen = freeze_corridor(narrow_wings,wing_graph,((25,19),2),widths,large_rooms=large_rooms)
    assert frozen and frozen.floor <= narrow_wings
    assert {c for c in wing_graph.axis if len(adjacent({c}) & wing_graph.axis) >= 3} <= frozen.spine
    for name,_ in large_rooms:
        assert any(p.approaches <= frozen.floor and not p.cells & frozen.floor
                   for p in room_option_geometry(name,frozenset(narrow_wings),None,True))
    wide_hull = rectangle(0,0,34,72)
    wide_graph = analyze(wide_hull)
    double = freeze_corridor(wide_hull,wide_graph,((17,0),2),widths)
    assert double and len(double.spine-wide_graph.axis) > 25

    # Two wide rooms separated by a neck that admits only one interior cell.
    shell = rectangle(0,0,8,12) | rectangle(12,0,20,12) | rectangle(8,5,12,7)
    guide = analyze(shell)
    port,rotation = (0,6),3
    endpoint = (19,6)
    terminal = Placement('test',0,0,0,frozenset(),frozenset(),frozenset(),frozenset(),
                         frozenset({endpoint}),0)
    for guided in (False,True):
        grid = regional_grid(shell,port,(1,2,3),4100,frozenset(),guide,
                             dict.fromkeys(guide.capacity,3),guided)
        route = regional_route((terminal,),port,rotation,grid)
        assert route and endpoint in route.clear and flood((1,6),route.clear) == route.clear
        assert any(w == 1 and x == 10 for (x,_),w,_,_ in route.sections)
        assert any(w == 3 for _,w,_,_ in route.sections)
        wide = regional_grid(shell,port,(2,3),4100,frozenset(),guide,
                             dict.fromkeys(guide.capacity,3),guided)
        assert regional_route((terminal,),port,rotation,wide) is None
    # Each nozzle is harmless by itself. Together they close both connections
    # to the pressure zone, so the CP-SAT cut must allow either neck to reopen.
    chamber = rectangle(0,0,4,1)
    lower = dict(cells={(2,0)},trim=set(),service={(1,0)})
    upper = dict(cells={(2,1)},trim=set(),service={(1,1)})
    check = MountTopology(chamber,(0,0),chamber,chamber,{(4,0)})
    assert check.conflicts([lower]) is None and check.conflicts([upper]) is None
    kind,_,frontier,_ = check.conflicts([lower,upper])
    assert kind == 'air' and frontier == {(2,0),(2,1)}
    trim = dict(cells=set(),trim={(2,0),(2,1)},service={(1,1)})
    assert check.conflicts([trim])[0] == 'foundation'
    corridor = {(x,0) for x in range(5)}
    check = MountTopology(chamber,(0,0),chamber,corridor,{(4,0)})
    assert check.conflicts([lower]) == ('access',None,{(2,0)},None)
    air = chamber | rectangle(0,3,4,3) | {(0,2),(4,2)}
    check = MountTopology(air,(0,0),air,chamber,set(),vents={(0,2),(4,2)})
    assert check.conflicts([lower,upper])[0] == 'heat'
    print('PASS: skeleton, legal width transitions and combined propulsion topology')


if __name__ == '__main__':
    main()
