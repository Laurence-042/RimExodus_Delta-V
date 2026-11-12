"""Offline regression for the task manifest and resource model."""
import argparse
from collections import Counter
from pathlib import Path
import json
import tempfile
import xml.etree.ElementTree as ET

from build import authoring_geometry, material_cost, read_defs
from generate_ships import read_shell
from hull_skeleton import analyze
from ship_program import PRESETS, catalog, choose, reward
from module_layout import adjacent


def accept_previews(results, baseline):
    """Run after the native preview fixture passes on each candidate."""
    def read(directory, name):
        return json.loads((directory/name).read_text(encoding='utf-8'))
    old = read(baseline,'AssemblyPlayerPreview.layout.json')
    cases = {}
    frozen_by_dock = {}
    for task in ('transport','production','escort'):
        directory = results/task
        batch = read(directory,'batch.json')
        assert batch['status'] == 'accepted' and len(batch['selected']) == 1, (task,batch['status'])
        layout = read(directory,'AssemblyPlayerPreview.layout.json')
        benefit = batch['selected'][0]['task']['raw']
        assert batch['playerShell'] == old['reference'], (task,'different starting hull')
        if 'rimCutouts' in layout:
            matching = [r for f in batch['feedback'] for r in f.get('frozenCorridors',())
                        if r['sections'] == layout['corridorSections']]
            assert matching, (task,'final corridor differs from every frozen plan')
            for route in matching:
                key = tuple(route['port']),route['rotation']
                assert frozen_by_dock.setdefault(key,route['sections']) == route['sections'], (
                    task,'different corridor for the same dock and hull')
        cases[task] = (layout,benefit)
    assert cases['transport'][1]['cargo'] > 48*len(old['cargoPaths'])
    assert cases['production'][1]['production'] > 0
    assert max(cases['escort'][1][n] for n in ('armament','defense')) > 0
    signatures = set()
    real_intersection = False
    for layout,_ in cases.values():
        cells = {tuple(c) for c in layout['centerline']}
        turns = []
        for _ in range(4):
            xmin,zmin = min(x for x,_ in cells),min(z for _,z in cells)
            turns.append(tuple(sorted((x-xmin,z-zmin) for x,z in cells)))
            cells = {(z,-x) for x,z in cells}
        signatures.add(min(turns))
        real_intersection |= any(len(adjacent({c}) & cells) >= 3 for c in cells)
    assert real_intersection
    if not frozen_by_dock:
        assert len(signatures) >= 2
    manifests = {tuple(sorted(Counter(n for n,*_ in layout['modules']).items()))
                 for layout,_ in cases.values()}
    assert len(manifests) >= 2
    print('PASS: three accepted task previews improve on the legacy baseline and obey their corridor policy')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('request',type=Path)
    parser.add_argument('data',type=Path)
    parser.add_argument('--results',type=Path,help='parent of transport, production and escort output folders')
    parser.add_argument('--baseline',type=Path,help='legacy replay output folder')
    parser.add_argument('--regional',action='store_true')
    args = parser.parse_args()
    if bool(args.results) != bool(args.baseline):
        parser.error('--results and --baseline must be supplied together')
    defs,parents,terrains,terrain_parents,_,_ = read_defs(args.data)
    geometry = authoring_geometry(defs,parents)
    rooms = catalog(defs,parents,geometry)
    terrain = sum((material_cost(terrains[n],terrain_parents)
                   for n in ('Substructure','MetalTile')),Counter())
    assert rooms['ModuleBridge'].tank > 0 and rooms['ModuleBridge'].beds == 0
    assert rooms['ModuleGenerator'].power > 0 and rooms['ModuleMachining'].power < 0
    assert rooms['ModuleFuel'].tank > rooms['ModuleFuel'].storage == 0
    assert rooms['ModulePointDefense'].storage >= 600
    assert reward(384,192)-reward(192,192) < reward(192,192)
    plans = {}
    for task in ('transport','production','escort'):
        shell = read_shell(args.request,task_override=task)
        skeleton = analyze(shell['cells'],shell['reserved']) if args.regional else None
        first,status = choose(shell,rooms,geometry,None,terrain,4100,1.65,2,skeleton=skeleton)
        assert first and status in ('OPTIMAL','FEASIBLE'), (task,status)
        assert (first,status) == choose(shell,rooms,geometry,None,terrain,4100,1.65,2,skeleton=skeleton)
        for plan in first:
            counts = Counter(plan['modules'])
            assert counts['ModuleBridge'] == 1 and counts['Galley'] >= 1
            assert sum(rooms[n].beds*v for n,v in counts.items()) >= shell['crewCount']
            if skeleton:
                assert len(plan['modules']) == len(plan['regions'])
                assert all(width == 0 or width in shell['widths'] for width in plan['widthPlan'].values())
        plans[task] = Counter(first[0]['modules'])
    assert plans['transport']['ModuleCargo'] > plans['production']['ModuleCargo']
    assert sum(plans['production'][n] for n in ('ModuleFabrication','ModuleResearch','ModuleMachining')) > (
        sum(plans['transport'][n] for n in ('ModuleFabrication','ModuleResearch','ModuleMachining')))
    assert sum(plans['escort'][n] for n in ('ModuleHeavyBattery','ModuleGuard','ModulePointDefense')) > (
        sum(plans['transport'][n] for n in ('ModuleHeavyBattery','ModuleGuard','ModulePointDefense')))
    assert choose(shell,rooms,geometry,{'GravlitePanel':0},terrain,4100,1.65,1) == ([], 'INFEASIBLE')
    doc = ET.parse(args.request)
    root = doc.getroot()
    root.set('task','balanced')
    root.set('widths','1')
    hull = root.find('./Hull/Cell')
    marks = root.find('Modules')
    mark = {**hull.attrib,'module':'ModuleCargo'}
    ET.SubElement(marks,'Cell',mark)
    ET.SubElement(marks,'Cell',mark)
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp)/'conflicting.shipplan'
        doc.write(path)
        try:
            read_shell(path)
        except ValueError:
            pass
        else:
            raise AssertionError('two independent zone markers at one cell must be rejected')
    assert shell['weights'] == PRESETS['escort']
    print('PASS: real Def accounting, task differences, repeatability, cost and marker constraints')
    if args.results:
        accept_previews(args.results,args.baseline)


if __name__ == '__main__':
    main()
