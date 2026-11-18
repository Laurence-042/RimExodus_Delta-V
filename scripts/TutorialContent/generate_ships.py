"""Offline module and propulsion search; accepted previews are never registered in a pool."""
import argparse
from collections import Counter
from hashlib import sha256
from itertools import combinations, islice
import json
import math
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
from time import perf_counter
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont, ImageOps

from build import ROOT, authoring_geometry, build, material_cost, read_defs
from hull_skeleton import analyze as analyze_hull
from fixed_corridor import freeze_corridor
from search_layout import SearchLayout
from ship_propulsion import (THRUST, armour_mount_band, armour_mount_slots, direction_capacity,
                             limits_from_native, mass_case, mount_proposals, mount_selections,
                             packed_armour_band, shell_mount_slots, trim_power)
from ship_program import PRESETS, PRESET_CREW, catalog, choose, measure, place_program
from ship_search import (DIR, MANDATORY, bounds, docking_sites, grow_layout, manifest, move, pack,
                         read_blueprint, route_proposals, target_mask)
from module_layout import MODULES, flood


WORKSHOP = Path('D:/SteamLibrary/steamapps/workshop/content/294100/3575162262/Gravship Blueprints')
DEFAULTS = [WORKSHOP / p for p in ('VGE Gravships/Freighter.btd','VGE Gravships/Explorer.btd','BTD Tier 0/Ebon_Hawk.btd')]
STAGE = ROOT / 'Source/obj/TutorialContent'


def progress(args, stage, detail='', step=0, total=0):
    if args.progress:
        print(f'SHIPPLAN\t{stage}\t{step}\t{total}\t{perf_counter()-args.started:.3f}\t{detail}',flush=True)


def save(path, data):
    path.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')


def motion(args, records_path):
    command = [str(args.native_dotnet),str(args.native),str(args.managed),'ship-motion',str(records_path),
               str(args.distance),str(args.harbor_distance)]
    result = subprocess.run(command,capture_output=True,text=True,encoding='utf-8')
    if result.returncode:
        raise RuntimeError(f'Native ship-motion failed: {result.stderr.strip()}')
    return json.loads(result.stdout)


def skeleton_diagram(path, skeleton, route=None, rooms=(), unused=()):
    scale, pad = 9, 16
    x0,z0,x1,z1 = bounds(skeleton.regions)
    image = Image.new('RGB',(scale*(x1-x0+1)+2*pad,scale*(z1-z0+1)+2*pad+24),'#141c25')
    draw = ImageDraw.Draw(image)
    palette = ('#365767','#495472','#4c6659','#675449','#534d66','#626244')
    def cell(c,color,inset=0,outline=None):
        x,z = pad+(c[0]-x0)*scale,pad+(z1-c[1])*scale
        draw.rectangle((x+inset,z+inset,x+scale-1-inset,z+scale-1-inset),fill=color,outline=outline)
    for c,i in sorted(skeleton.regions.items()):
        cell(c,palette[i % len(palette)])
    for c in unused:
        cell(c,'#935465')
    for p in rooms:
        for c in p.cells:
            cell(c,'#aab7c4',1)
    for c in skeleton.raw:
        cell(c,'#8596ac',3)
    for c in skeleton.axis:
        cell(c,'#79e2cd',3)
    if route:
        for c in route.floor:
            cell(c,None,1,'#d4a359')
        for c in route.spine:
            cell(c,'#ffd180',2)
        for p in rooms:
            for c in p.approaches:
                cell(c,'#ee8d72',2)
    draw.text((pad,image.height-31),'teal: skeleton   gold: route   pink: unused',fill='white')
    image.save(path)


def budget_rejection(report, limits, exact=False, task=False):
    if report['generationW']-report['nameplateLoadW']-report.get('rechargeW',0) < 100:
        return 'POWER_MARGIN'
    if report['batteryCapacityWd'] < 600:
        return 'BATTERY'
    if report['fuelLoaded'] < 6*report['generatorFuelPerDay']:
        return 'GENERATOR_FUEL'
    if report['uraniumLoaded'] < 6*report['uraniumPerDay']:
        return 'REACTOR_FUEL'
    if (not task and (len(report['cargoPaths']) < 4 if exact else len(report['cargoPaths']) != 4)) or any(
            p is None for p in report['cargoPaths']):
        return 'CARGO_ACCESS'
    if limits is not None:
        for name,count in report['materials'].items():
            if count > limits.get(name,math.inf if exact else 0):
                return f'MATERIAL:{name}'
    return None


def motion_rejection(native, limits, report):
    for k,case in enumerate(native):
        for i,row in enumerate(case['axes']):
            if row['acceleration'] + 1e-6 < limits[k][i]:
                return f"AUTHORITY:{case['stacks']}:{row['axis']}"
            if row['residual'] > .1:
                return f"RESIDUAL:{case['stacks']}:{row['axis']}"
    required = max(native[-1][key]['requiredFuel'] for key in ('route','harbor'))
    if min(report['propulsionTankCapacity'],report['propulsionFuelLoaded']) < required:
        return 'PROPULSION_FUEL'
    return None


def similarity(a,b):
    return len(a&b)/len(a|b)


def footprint_set(report):
    return {tuple(c) for c in report['foundation']}


def outline_features(target, actual):
    """Compare the reference's transverse width changes and off-axis negative space."""
    x0,z0,x1,z1 = bounds(target)
    rows = [(sum((x,z) in target for x in range(x0,x1+1)),
             sum((x,z) in actual for x in range(x0-4,x1+5))) for z in range(z0,z1+1)]
    step = max(2,len(rows)//7)
    bands = [tuple(sum(row[i] for row in rows[j:j+step])/len(rows[j:j+step]) for i in range(2))
             for j in range(0,len(rows),step)]
    core = bands[1:-1] if len(bands) >= 5 else bands
    mean = [sum(row[i] for row in core)/len(core) for i in range(2)]
    covariance = sum((a-mean[0])*(b-mean[1]) for a,b in core)
    variance = math.sqrt(math.prod(sum((row[i]-mean[i])**2 for row in core) for i in range(2)))
    error = sum(abs(a-b) for a,b in bands)/sum(a for a,b in bands)
    missing = set()
    for z in range(z0,z1+1):
        occupied = [x for x in range(x0,x1+1) if (x,z) in target]
        if occupied:
            missing.update((x,z) for x in range(min(occupied),max(occupied)+1) if (x,z) not in target)
    asymmetry = {(x0+x1-x,z) for x,z in target} - target
    return dict(widthCorrelation=covariance/variance if variance else 0, widthError=error,
                gapCells=len(missing), gapRetention=len(missing-actual)/max(1,len(missing)),
                asymmetricCells=len(asymmetry), notchRetention=len(asymmetry-actual)/max(1,len(asymmetry)),
                widths=[[round(a,2),round(b,2)] for a,b in bands])


def appearance(layout):
    xmin,_,xmax,_ = bounds(layout.symmetry_reference)
    keys = {(e['def'],e['rotation'],e['cells']) for e in layout.engines}
    unpaired = sum((kind,(-r)%4,frozenset((xmin+xmax-x,z) for x,z in cells)) not in keys
                   for kind,r,cells in keys)
    return unpaired,sum(len(e['extension']) for e in layout.engines)


def select_final(candidates, guided):
    if 'task' in candidates[0]:
        if candidates[0]['layout'].fixed_corridor:
            def fixed_priority(row):
                rooms = row['layout'].packed['placements']
                useful = sum(len(p.cells-p.wall-p.doors-p.vents) for p in rooms)
                shared = sum(len(p.wall) for p in rooms)-len(set().union(*(p.wall for p in rooms)))
                opened = sum(len(p.cut) for p in rooms)
                return (-useful,-(shared+opened),-row['task']['score'],len(row['deck']['unused']),
                        row['cargoDistance'],row['materialTotal'],appearance(row['layout']),-row['overlap'])
            return min(candidates,key=fixed_priority)
        best = max(row['task']['score'] for row in candidates)
        candidates = [row for row in candidates if row['task']['score'] == best]
        return min(candidates,key=lambda row:(len(row['deck']['unused']),row['cargoDistance'],
            row['materialTotal'],row['native'][-1]['route']['fuel'],-row['forward'],appearance(row['layout'])))
    best_overlap = max(row['overlap'] for row in candidates)
    if guided:
        candidates = [row for row in candidates if row['overlap'] >= best_overlap-.02]
    best_thrust = max(row['forward'] for row in candidates)
    candidates = [row for row in candidates if row['forward'] >= .95*best_thrust]
    result = min(candidates,key=lambda row:(appearance(row['layout']),-row['forward'],
                                            -row['overlap'] if guided else 0))
    result.update(bestFeasibleOverlap=best_overlap,bestFeasibleForward=best_thrust)
    return result


def compare_previews(directory, batch):
    selected = batch['selected']
    layouts = [json.loads((directory/(row['name']+'.layout.json')).read_text(encoding='utf-8')) for row in selected]
    signatures = []
    for layout in layouts:
        cells = footprint_set(layout)
        x,z,_,_ = bounds(cells)
        signatures.append(({(a-x,b-z) for a,b in cells},
                           {(name,a-x,b-z,rotation) for name,a,b,rotation in layout['modules']}))
    batch['comparisons'] = [dict(first=selected[i]['name'],second=selected[j]['name'],
        outlineDifference=1-similarity(signatures[i][0],signatures[j][0]),
        modulePlacementDifference=1-similarity(signatures[i][1],signatures[j][1]))
        for i,j in combinations(range(len(selected)),2)]
    if len(selected) < 2:
        return
    pane = 520
    chart = Image.new('RGB',(pane*len(selected),655),'#111b28')
    text = ImageDraw.Draw(chart)
    font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',19)
    for i,row in enumerate(selected):
        with Image.open(directory/(row['name']+'.png')) as image:
            panel = ImageOps.contain(image,(pane-12,555))
        chart.paste(panel,(pane*i+(pane-panel.width)//2,48+(555-panel.height)//2))
        label = row['blueprint'] if row['guided'] else '关闭外形引导'
        text.text((pane*i+18,18),f"{label}  |  {row['topology']}",font=font,fill='white')
        text.text((pane*i+18,608),f"IoU {row['overlap']:.1%}   前推 {row['forward']:.2f} 格/秒²",font=font,fill='#c2d3e2')
    chart.save(directory/'Comparison.png')


def search(args, path, modules, limits, cap, guided, temporary, shell=None, feedback=None,
           skeleton=None, program=None):
    raw = read_blueprint(path) if shell is None else shell['cells']
    # Scale with the requested program, not with the Workshop blueprint's cost.
    area = 470 + 45*(len(modules)-7)
    target = target_mask(raw,area) if shell is None else raw
    defs, parents, terrains, terrain_parents, _, _ = read_defs(args.data)
    geometry = authoring_geometry(defs,parents)
    fixed_corridor = bool(skeleton and guided)
    reason = Counter()
    def reject(key):
        reason[key] += 1
        if args.trace:
            print(f'  {n} {route.topology if route else "Rooms"}: {key}',flush=True)
    candidates = []
    propulsion_slots = None
    thrust_need = None
    if skeleton and not fixed_corridor:
        propulsion_slots = shell_mount_slots(geometry,target,shell['trimmable'],
                                             shell['reserved'] | shell['deckReserved'])
        cargo = 2*modules.count('ModuleCargo')
        typical_mass = program['estimate']['loadedMass'] - 1350*cargo + 3*len(target)
        slot_force = propulsion_slots[3]
        thrust_need = tuple(math.ceil(limits[1][i]*typical_mass/max(slot_force,1e-6)) for i in (2,0,3,1))
    route_target = target if shell or guided else target_mask({(x,z) for x in range(25) for z in range(23)},area)
    queue = [(r,(),None) for r in islice(route_proposals(route_target,args.seed),args.proposals)] if not shell else []
    armour_slots = None
    planned_fuel_rate = None
    if fixed_corridor and not args.late_propulsion:
        progress(args,'RoomModel','enumerating two-layer shell installation bands')
        armour_slots = armour_mount_slots(geometry,target,shell['reserved'] | shell['deckReserved'],shell['trimmable'])
        # A conservative sizing allowance for imperfect allocation. Actual
        # tank capacity and departure fuel still have to pass the native route.
        drag_floor = math.ceil(.00011*60*(2**(1/480)-1)*max(args.distance,args.harbor_distance)*1000)
        planned_fuel_rate = max(math.ceil(program['fuelRate']*1.15),math.ceil(drag_floor*1.15/.67))
    def demanded():
        sites = list(docking_sites(target,shell['reserved'] | shell['deckReserved']))
        if not sites:
            yield None,(),dict(status='NO_DOCK')
            return
        a,b,c,d = bounds(target)
        x,z = (a+c)//2,(b+d)//2
        sites.sort(key=lambda s:(s[1],abs(s[0][0]-x)+abs(s[0][1]-z),s[0]))
        grouped = [[s for s in sites if s[1] == r] for r in (2,1,3,0)]
        docks = [group[i] for i in range(max(map(len,grouped))) for group in grouped if i < len(group)]
        widths = sorted(shell['widths'],key=(lambda w:w) if shell['task'] else (lambda w:(abs(w-2),-w)))
        for attempt in range(args.proposals):
            dock_index = attempt+getattr(args,'dock_offset',0) if skeleton else attempt//len(widths)
            dock = docks[dock_index % len(docks)]
            progress(args,'Corridor',f'dock={dock}',attempt+1,args.proposals)
            if fixed_corridor:
                mandatory = [('ModuleBridge',None),('Galley',None)]
                for name,anchor in shell['marks']:
                    if (name,None) in mandatory:
                        mandatory.remove((name,None))
                    mandatory.append((name,anchor))
                if not any(name == 'ModuleFuel' for name,_ in mandatory):
                    mandatory.append(('ModuleFuel',None))
                frozen = freeze_corridor(target,skeleton,dock,widths,shell['deckReserved'],mandatory)
                if frozen is None:
                    yield None,(),dict(status='NO_FIXED_CORRIDOR')
                    continue
                if feedback is not None:
                    feedback.setdefault('frozenCorridors',[]).append(
                        dict(port=dock[0],rotation=dock[1],sections=frozen.sections))
                def room_progress(stage, detail):
                    progress(args,stage,detail,attempt+1,args.proposals)
                band = None
                fuel_rate = program['fuelRate']
                if armour_slots is not None:
                    band = armour_mount_band(armour_slots,frozen)
                    fuel_rate = planned_fuel_rate
                    room_progress('RoomModel',f'armour_slots={len(band)} fuel_per_1000_mass={fuel_rate}')
                for solution in place_program(shell,frozen,program['catalog'],geometry,cap,
                                              program['terrainCost'],args.seed+attempt*47,
                                              args.layout_budget,args.programs,
                                              max(fuel_rate,(feedback or {}).get('fuelRate',0)),feedback,
                                              room_progress if args.progress else None,band,limits):
                    if band is not None and 'placements' in solution:
                        solution['armourBand'] = packed_armour_band(band,frozen,solution['placements'])
                        budget = solution['estimate']
                        room_progress('RoomSolve',f'edge_budget={[budget[f"mounts{r}"] for r in range(4)]} '
                                      f'loaded_mass={budget["loadedMass"]} tank={budget["tank"]} '
                                      f'reserved_slots={len(solution["armourBand"])}')
                    yield frozen,(),solution
                continue
            width = tuple(widths) if skeleton else widths[attempt % len(widths)]
            seed = args.seed + attempt*47
            found = False
            diagnostic = {}
            for fixed,route in grow_layout(modules,target,shell['anchors'],shell['reserved'],
                                           dock,width,seed,geometry,task=bool(shell.get('task')),
                                           deck_reserved=shell['deckReserved'],skeleton=skeleton,
                                           assignments=program['regions'] if skeleton else None,
                                           width_plan=program['widthPlan'] if skeleton else None,guided=guided,
                                           diagnostics=diagnostic if skeleton else None,
                                           propulsion_slots=propulsion_slots,thrust_need=thrust_need,
                                           propulsion_weight=int(attempt%2 == 0)):
                packed = pack(modules,route,target,True,seed,args.layout_budget,len(target),
                              geometry,anchors=shell['anchors'],reserved=shell['reserved'],fixed=fixed)
                if 'placements' in packed:
                    found = True
                    yield route,(),packed
            if not found:
                if diagnostic and args.trace:
                    print(f'  dock {dock[0]}: {diagnostic}',flush=True)
                if diagnostic and feedback is not None:
                    feedback.setdefault('blockedRegions',[]).append(diagnostic)
                yield None,(),dict(status='NO_CONNECTED_CORRIDOR')
    retries = 0
    def retry(n,route,previous,packed):
        nonlocal retries
        if shell or retries >= 4:
            return
        queue.append((route,(*previous,tuple(packed['placements'])),None))
        retries += 1
    for n,(route,rejected_layouts,packed) in enumerate(demanded() if shell else queue):
        if args.require_junction and not shell and route.topology not in ('T','Cross','H'):
            continue
        if not shell:
            packed = pack(modules,route,target,guided,args.seed+n,args.layout_budget,
                          cap.get('GravlitePanel',9999)-14,geometry,rejected_layouts)
        if not packed or 'placements' not in packed:
            reject('PACK:'+(packed['status'] if packed else 'NO_PORT_SLOTS'))
            if args.trace and packed and packed.get('missing'):
                detail = ('no placements clear of retained markers for: ' if packed['status'] == 'NO_MARKER_SLOTS'
                          else 'no room entrances for: ')
                print('    '+detail+', '.join(packed['missing']),flush=True)
            continue
        progress(args,'Services',f'candidate={n+1} rooms={len(packed["placements"])}')
        layout = SearchLayout(packed,route,target,route_target,bool(shell),shell['outsideTemp'] if shell else -100,
                              shell['reserved'] if shell else frozenset(),
                              shell['deckReserved'] if shell else frozenset(),
                              task=bool(shell and shell.get('task')),fixed_corridor=fixed_corridor,
                              trimmable=shell['trimmable'] if shell else frozenset(),
                              armour_band=packed.get('armourBand'))
        if skeleton and feedback is not None:
            occupied = route.floor | set().union(*(p.cells for p in packed['placements']))
            uncovered = set(target-occupied)
            largest = 0
            while uncovered:
                component = flood(min(uncovered),uncovered)
                uncovered -= component
                largest = max(largest,len(component))
            snapshot = dict(topology=route.topology,spine=sorted(route.spine),sections=route.sections,
                            estimatedUnused=len(target-occupied),estimatedLargestBlock=largest,
                            rooms=[(p.module,p.x,p.z,p.rot) for p in packed['placements']])
            if len(feedback.get('closest',{}).get('spine',())) == 0 or (
                    snapshot['estimatedUnused'] < feedback['closest']['estimatedUnused']):
                feedback['closest'] = snapshot
                if args.output:
                    skeleton_diagram(args.output/f'layout-attempt-{args.seed}.png',skeleton,route,packed['placements'],
                                     target-occupied)
        if args.require_junction and layout.route.topology not in ('Junction','Cross'):
            reject('NO_USED_JUNCTION')
            continue
        try:
            report,records = build(args.data,'AssemblySearchCandidate',layout_override=layout,evaluate_only=True)
        except AssertionError as exc:
            reject('LAYOUT:'+str(exc.args[0][:2] if isinstance(exc.args[0],tuple) else exc.args[0]))
            if args.trace and exc.args and isinstance(exc.args[0],tuple) and len(exc.args[0]) > 2:
                print(f'    details: {exc.args[0][2:]}',flush=True)
            if shell: retry(n,route,rejected_layouts,packed)
            continue
        if shell and not reserved_markers_fit(shell,report,records):
            reject('MARKER:BLOCKED')
            retry(n,route,rejected_layouts,packed)
            continue
        task = bool(shell and shell.get('task'))
        if failure := budget_rejection(report,cap,bool(shell),task):
            reject(failure)
            continue
        hull,walls,walkable = layout.mount_inputs
        topology = layout.mount_topology if task else None
        if topology:
            topology.unused = frozenset(report['deck']['unused'])
            topology.max_unused_block = shell['maxUnusedBlock']
        banks = layout.engine_banks if task else ()
        progress(args,'Mounts',f'candidate={n+1}')
        mounts = mount_proposals(geometry,defs,parents,terrains,terrain_parents,hull,walls,walkable,
                                 route.port,route.rotation,layout.inset if shell and layout.armour_band is None else frozenset())
        if layout.armour_band is not None:
            mounts = [m for m in mounts
                      if (slot := layout.armour_band.get((m['def'],m['rotation'],m['origin']))) is not None
                      and (m['face'],m['extension'],m['exclusion'] & target) == slot[:3]]
        if shell:
            retained = footprint_set(report)
            mounts = [dict(m,trim=m['exclusion'] & retained) for m in mounts
                      if m['cells'] <= retained and not m['cells'] & shell['reserved']
                      and (m['exclusion'] & retained) <= shell['trimmable']]
            mounts = [m for m in mounts if not m['trim'] or
                      flood(route.port,retained-m['trim']) == retained-m['trim']]
        if args.trace:
            mounts_by_direction = [sum(e['rotation'] == d for e in mounts) for d in range(4)]
            print(f'  {n} {route.topology}/{route.width}: {len(hull)} hull / '
                  f'{len(mounts)} mount candidates N/E/S/W={mounts_by_direction}',flush=True)
        if task:
            trim_limit = min(60,len(target)//12)
            covered = set().union(*(e['cells'] for e in mounts))
            cuts = set().union(*(e['trim'] for e in mounts))
            unavoidable = topology.unused-covered-cuts
            oversized = topology.oversized_deck(unavoidable)
            remaining = topology.unused-covered
            best_trim = min(trim_limit,len(remaining & cuts))
            if oversized or (
                    100*(len(remaining)-best_trim) > shell['maxUnused']*(len(topology.foundation)-best_trim)):
                reject('DECK:MOUNT_CAPACITY')
                if args.trace:
                    print(f'    optimistic unused {len(remaining)-best_trim}, oversized mandatory block {len(oversized)}',flush=True)
                continue
            upper = direction_capacity(mounts,trim_limit,.025,report['flightRotation'])
            # Optimistic mass bound. A failure here cannot be repaired by
            # assigning throttle or by a different combination of these mounts.
            mass_change = sum(min(0,e['mass']) for e in mounts)-trim_limit
            masses = [mass_case(report,records,stacks)[0]+mass_change for stacks in (0,12,48)]
            need = [max(math.ceil(floors[axis]*mass/THRUST)
                        for floors,mass in zip(limits,masses)) for axis in range(4)]
            if any(capacity < minimum for capacity,minimum in zip(upper,need)):
                reject('PROPULSION:DIRECTIONAL_CAPACITY')
                if args.trace:
                    print(f'    east/west/forward/brake ceilings {upper}, minimum {need}',flush=True)
                continue
        rejected, shift = [], (0.,0.)
        placements = None
        for attempt in range(args.engine_attempts):
            def engine_progress(stage, detail):
                progress(args,stage,f'candidate={n+1} {detail}',attempt+1,args.engine_attempts)
            if placements is None or layout.armour_band is None:
                placements = mount_selections(mounts,route_target,hull,report,records,limits,cap,args.seed+n,
                                     args.engine_budget,guided and not shell,rejected,shift,
                                     min(60,len(target)//12) if shell else 0,task=task,
                                     topology=topology,banks=banks,
                                     max_unused_percent=shell['maxUnused'] if task else None,
                                     angular_priority=fixed_corridor,
                                     progress=engine_progress if args.progress else None,
                                     attempts=args.engine_attempts if layout.armour_band is not None else 1)
            selected = next(placements)
            progress(args,'Fit',f'candidate={n+1} status={selected["status"]} '
                     f'appearance={selected.get("appearanceStatus")} conflicts={selected.get("topologyRejections")}',
                     attempt+1,args.engine_attempts)
            for kind,count in selected.get('topologyRejections',{}).items():
                reason['PROPULSION_TOPOLOGY:'+kind] += count
            if 'indexes' not in selected:
                reject('PROPULSION:'+selected['status'])
                if skeleton and args.trace and selected['status'] == 'INFEASIBLE':
                    upper = direction_capacity(mounts,min(60,len(target)//12),.02,report.get('flightRotation',0))
                    mass = mass_case(report,records,12)[0]
                    need = [math.ceil(a*mass/THRUST) for a in limits[1][:4]]
                    print(f'    east/west/forward/brake independent mount ceiling {upper}; '
                          f'force units at typical load before mounting {need}',flush=True)
                break
            indexes = selected['indexes']
            rejected.append(set(indexes))
            layout.engines = [dict(mounts[i]) for i in indexes]
            try:
                final,placed = build(args.data,'AssemblySearchCandidate',layout_override=layout,evaluate_only=True)
            except AssertionError as exc:
                reject('INSTALL:'+str(exc.args[0][:2] if isinstance(exc.args[0],tuple) else exc.args[0]))
                continue
            if shell and not reserved_markers_fit(shell,final,placed):
                reject('MARKER:BLOCKED')
                continue
            actual = footprint_set(final)
            overlap = similarity(target,actual)
            features = outline_features(target,actual)
            if guided and not shell and (overlap < .65 or features['widthCorrelation'] < .25 or features['widthError'] > .35
                           or (features['gapCells'] >= 5 and features['gapRetention'] < .25)
                           or (features['asymmetricCells'] >= 16 and features['notchRetention'] < .25)):
                reject(f'OUTLINE:{overlap:.3f}:{features["widthCorrelation"]:.2f}')
                continue
            if task and (100*len(final['deck']['unused']) > len(actual)*shell['maxUnused']
                         or final['deck']['largestUnused'] > shell['maxUnusedBlock']):
                reject('DECK:UNUSED')
                continue
            if failure := budget_rejection(final,cap,bool(shell),task):
                reject(failure)
                continue
            records_path = temporary/'Candidate.records.json'
            save(records_path,placed)
            save(temporary/'Candidate.layout.json',final)
            progress(args,'Flight',f'candidate={n+1} engines={len(indexes)}')
            native = motion(args,records_path)
            failure = motion_rejection(native,limits,final)
            if failure and failure.startswith(('AUTHORITY:','RESIDUAL:')):
                progress(args,'Flight',f'candidate={n+1} throttle retry: {failure}')
                power = trim_power(layout.engines,final,placed,limits,args.seed+n,.60 if shell else .40,
                                   native if shell else None,task=task)
                if power:
                    for engine,percent in zip(layout.engines,power):
                        engine['power'] = percent
                    final,placed = build(args.data,'AssemblySearchCandidate',layout_override=layout,evaluate_only=True)
                    save(records_path,placed)
                    save(temporary/'Candidate.layout.json',final)
                    native = motion(args,records_path)
                    failure = motion_rejection(native,limits,final)
            if failure:
                reject(failure)
                if failure == 'PROPULSION_FUEL' and feedback is not None:
                    required = math.ceil(max(native[-1][key]['requiredFuel'] for key in ('route','harbor')))
                    if args.trace:
                        print(f'    propulsion fuel required={required} loaded={final["propulsionFuelLoaded"]:.0f}',flush=True)
                    feedback['tankTarget'] = min(required,feedback.get('tankTarget',required))
                    if fixed_corridor:
                        rate = math.ceil(1000*required/packed['estimate']['loadedMass'])
                        feedback['fuelRate'] = max(rate,feedback.get('fuelRate',0))
                _,cx,cz,_,_ = mass_case(report,records,12)
                mx,mz = native[1]['center']
                shift = (mx-cx,mz-cz)
                continue
            if task:
                task_result = measure(shell,final,placed,geometry[0])
                if final['batteryCapacityWd'] < task_result['burstEnergyWd']:
                    reject('BURST_ENERGY')
                    continue
            print(f'  {n}: {route.topology}, {overlap:.3f} IoU, {native[1]["axes"][2]["acceleration"]:.2f} forward',flush=True)
            candidates.append(dict(layout=layout,native=native,overlap=overlap,features=features,
                                   forward=native[1]['axes'][2]['acceleration'],solver=selected,
                                   packing={k:v for k,v in packed.items() if k not in ('placements','armourBand')}))
            if task:
                candidates[-1].update(task=task_result,deck=final['deck'],
                    cargoDistance=sum(final['cargoPaths']),materialTotal=sum(final['materials'].values()),
                    resources=dict(powerMarginW=final['generationW']-final['nameplateLoadW']-final['rechargeW'],
                                   batteryWd=final['batteryCapacityWd'],
                                   generationFuelSurplus=final['fuelLoaded']-6*final['generatorFuelPerDay'],
                                   reactorFuelSurplus=final['uraniumLoaded']-6*final['uraniumPerDay'],
                                   propulsionFuelSurplus=final['propulsionFuelLoaded']-max(
                                       native[-1][k]['requiredFuel'] for k in ('route','harbor'))))
            if layout.armour_band is not None:
                candidates[-1]['propulsionPlan'] = dict(
                    directionalBudget=[packed['estimate'][f'mounts{r}'] for r in range(4)],
                    reservedMounts=len(layout.armour_band), fitMounts=len(mounts),
                    installed=[sum(engine['rotation'] == r for engine in layout.engines) for r in range(4)],
                    estimatedFullMass=packed['estimate']['loadedMass'], tank=packed['estimate']['tank'])
            break
        if not any(row['layout'] is layout for row in candidates):
            # Installation/flight failures exclude that combination of module
            # slots when this route is retried; the next hull exposes other walls.
            retry(n,route,rejected_layouts,packed)
        if len(candidates) >= args.finalists:
            break
    return candidates,reason


def reserved_markers_fit(shell, report, records):
    occupied = {tuple(c) for r in records if r['layer'] == 'body' for c in r['cells']}
    roofs = {tuple(c) for c in report['roofs']}
    return (not occupied & shell['reserved'] and shell['crew'] <= roofs and
            not (occupied | roofs) & shell['deckReserved'])


def read_shell(path, force_legacy=False, task_override=None):
    document = ET.parse(path).getroot()
    explicit_task = task_override or document.get('task')
    legacy = force_legacy or explicit_task is None
    def cell(node):
        return int(node.attrib['x']),int(node.attrib['z'])
    cells = {cell(n) for n in document.findall('./Hull/Cell')}
    if not 1 <= len(cells) <= 1600 or min(min(c) for c in cells) < 0 or max(max(c) for c in cells) > 110:
        raise ValueError('Hull is empty or too large for the authoring solver')
    if flood(min(cells),cells) != cells:
        raise ValueError('Hull must be four-neighbor connected')
    widths = tuple(int(w) for w in document.attrib['widths'].split(','))
    outside = float(document.attrib['outsideTemp'])
    if not widths or any(w not in (1,2,3) for w in widths) or not math.isfinite(outside):
        raise ValueError('Invalid corridor width or outdoor temperature')
    modules,anchors = list(MANDATORY),[None]*len(MANDATORY)
    marks = []
    for mark in document.findall('./Modules/Cell'):
        module,c = mark.attrib['module'],cell(mark)
        if module not in (MODULES.keys() | {'Galley'}) or c not in cells:
            raise ValueError(f'Invalid module marker: {module}, {c}')
        marks.append((module,c))
        if not legacy:
            continue
        available = next((i for i,v in enumerate(modules) if v == module and anchors[i] is None),None)
        if available is not None:
            anchors[available] = c
        elif module != 'ModuleBridge':
            modules.append(module)
            anchors.append(c)
        else:
            raise ValueError('Only one bridge marker can be used per ship')
    if len(modules) > 18:
        raise ValueError('Too many module markers')
    reserved = {cell(n) for n in document.findall('./Reserved/Cell')}
    deck_reserved = {cell(n) for n in document.findall('./DeckReserve/Cell')}
    crew = {cell(n) for n in document.findall("./Reserved/Cell[@crew='1']")}
    trimmable = {cell(n) for n in document.findall('./Trimmable/Cell')}
    if not (reserved | deck_reserved) <= cells:
        raise ValueError('A retained marker is not on the hull')
    task = explicit_task or 'balanced'
    if task not in PRESETS:
        raise ValueError(f'Unknown ship task: {task}')
    crew_count = int(document.get('crewCount',max(PRESET_CREW[task],len(crew))))
    weights = (PRESETS[task] if task_override else
               tuple(int(w) for w in document.get('weights',','.join(map(str,PRESETS[task]))).split(',')))
    max_unused = int(document.get('maxUnused','20'))
    max_block = int(document.get('maxUnusedBlock','64'))
    materials = {m.attrib['def']:int(m.attrib['count']) for m in document.findall('./Materials/Limit')}
    route_distance = float(document.get('distance',20000))
    harbor_distance = float(document.get('harborDistance',300))
    if (not 1 <= crew_count <= 24 or crew_count < len(crew) or len(weights) != 4
            or not all(0 <= w <= 1000 for w in weights) or not sum(weights)
            or not 0 <= max_unused <= 100 or not 0 <= max_block <= 1600
            or len(marks) > 18 or sum(n == 'ModuleBridge' for n,_ in marks) > 1
            or len({c for _,c in marks}) != len(marks)
            or any(not math.isfinite(v) or v <= 0 for v in (route_distance,harbor_distance))
            or any(v < 0 for v in materials.values())):
        raise ValueError('Invalid ship program or material limit')
    return dict(cells=cells,widths=widths,outsideTemp=outside,anchors=anchors,
                modules=modules,reserved=reserved,crew=crew,
                marks=marks,task=None if legacy else task,crewCount=crew_count,weights=weights,
                distance=route_distance,harborDistance=harbor_distance,
                maxUnused=max_unused,maxUnusedBlock=max_block,materials=materials,
                deckReserved=deck_reserved,trimmable=(trimmable & cells)-reserved-deck_reserved)


def cli():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blueprint',type=Path,action='append',help='repeat to compare masks')
    parser.add_argument('--shell-request',type=Path,help='solve the exact player hull from a Delta-V authoring request')
    parser.add_argument('--shell-task',choices=PRESETS,help='override the player request task preset')
    parser.add_argument('--legacy-shell',action='store_true',help='replay the fixed seven-room shell baseline')
    parser.add_argument('--no-skeleton-guide',action='store_true',
                        help='use the previous demand-routed control for task comparisons')
    parser.add_argument('--late-propulsion',action='store_true',help='compare with propulsion planning after room packing')
    parser.add_argument('--programs',type=int,default=4,help='maximum candidate room programs for an exact shell')
    parser.add_argument('--data',type=Path,default=Path('D:/SteamLibrary/steamapps/common/RimWorld_1.6/Data'))
    parser.add_argument('--output',type=Path,default=STAGE/'ShipSearch')
    parser.add_argument('--native',type=Path,default=ROOT/'Source/obj/TutorialRegression/TutorialRegression.dll')
    parser.add_argument('--native-dotnet',default='dotnet',help='runtime used for the native fixture')
    parser.add_argument('--managed',type=Path,default=Path('D:/SteamLibrary/steamapps/common/RimWorld_1.6/RimWorldWin64_Data/Managed'))
    parser.add_argument('--seed',type=int,default=4100)
    parser.add_argument('--weights',type=float,nargs=3,default=(30,50,20))
    parser.add_argument('--material-limits',type=Path,help='JSON object overriding the default per-material limits')
    parser.add_argument('--distance',type=float)
    parser.add_argument('--harbor-distance',type=float)
    parser.add_argument('--layout-budget',type=float,default=1.65,help='deterministic CP-SAT time per layout solve')
    parser.add_argument('--engine-budget',type=float,default=1.5,help='deterministic CP-SAT time per propulsion solve')
    parser.add_argument('--engine-attempts',type=int,default=4)
    parser.add_argument('--proposals',type=int,default=34)
    parser.add_argument('--finalists',type=int,default=2)
    parser.add_argument('--blind-control',action='store_true')
    parser.add_argument('--require-junction',action='store_true',help='require three or four used corridor branches')
    parser.add_argument('--batch-seeds',type=int,default=0,help='also test this many consecutive seeds on the first blueprint')
    parser.add_argument('--batch-solves',type=int,default=1,help='number of drawn seeds per role to actually solve')
    parser.add_argument('--trace',action='store_true',help='print proposal rejection reasons as they occur')
    parser.add_argument('--progress',action='store_true',help='emit machine-readable ship planner milestones')
    args = parser.parse_args()
    args.started = perf_counter()
    progress(args,'Request',f'seed={args.seed} docks={args.proposals} programs={args.programs} '
             f'layout_det={args.layout_budget} engine_det={args.engine_budget} attempts={args.engine_attempts}')
    shell = (read_shell(args.shell_request,args.legacy_shell,args.shell_task) if args.shell_request else None)
    if args.distance is None:
        args.distance = shell['distance'] if shell else 20000
    if args.harbor_distance is None:
        args.harbor_distance = shell['harborDistance'] if shell else 300
    if (any(not math.isfinite(w) or w < 0 for w in args.weights) or not sum(args.weights)
            or any(not math.isfinite(v) or v <= 0 for v in (args.distance,args.harbor_distance,args.layout_budget,args.engine_budget))
            or min(args.proposals,args.finalists,args.engine_attempts,args.batch_solves,args.programs) < 1 or
            args.seed < 0 or args.batch_seeds < 0):
        parser.error('weights must be nonnegative with a positive total; budgets and distances must be positive')
    common = (ROOT/'Common/Prefabs').resolve()
    if args.output.resolve().is_relative_to(common):
        parser.error('output must be outside the installed prefab catalog')
    if not args.native.is_file():
        parser.error('compile the native fixture with scripts/TutorialRegression/run.ps1 -ModulesOnly first')
    role,modules = manifest(args.seed,args.weights)
    progress(args,'Reference','resolve reference prefab')
    reference, reference_records = build(args.data,'AssemblyIndustrial',evaluate_only=True)
    cap = dict(reference['materials'])
    cap['ComponentIndustrial'] += 2
    if args.material_limits:
        overrides = json.loads(args.material_limits.read_text(encoding='utf-8-sig'))
        if not isinstance(overrides,dict) or any(type(v) is not int or v < 0 for v in overrides.values()):
            parser.error('material limits must map Def names to nonnegative integers')
        cap.update(overrides)
    if not args.shell_request:
        print(f'{role}: {", ".join(modules)}',flush=True)
    args.output.mkdir(parents=True,exist_ok=True)
    with TemporaryDirectory() as tmp:
        temporary = Path(tmp)
        save(temporary/'Reference.layout.json',reference)
        save(temporary/'Reference.records.json',reference_records)
        progress(args,'Reference','native reference flight')
        baseline = motion(args,temporary/'Reference.records.json')
        limits = limits_from_native(baseline)
        if shell:
            legacy_shell = not shell['task']
            cap = dict(shell['materials'])
            if args.material_limits:
                cap.update(overrides)
            cap = cap or None
            programs = [dict(modules=shell['modules'],anchors=shell['anchors'])]
            program_status = None
            proven_failure = None
            feedback_history = []
            skeleton = None
            guidance = not args.no_skeleton_guide
            if shell['task']:
                progress(args,'Skeleton',f'task={shell["task"]} cells={len(shell["cells"])}')
                shell.update(distance=args.distance,harborDistance=args.harbor_distance)
                defs,parents,terrains,terrain_parents,_,_ = read_defs(args.data)
                geometry = authoring_geometry(defs,parents)
                terrain_cost = sum((material_cost(terrains[n],terrain_parents)
                                    for n in ('Substructure','MetalTile')),Counter())
                size = len(shell['cells'])
                min_area = size-min(60,size//12,len(shell['trimmable']))
                proven_failure = next((f'FOUNDATION_MATERIAL:{n}' for n,c in sorted((cap or {}).items())
                                       if min_area*terrain_cost[n] > c),None)
                if proven_failure:
                    programs = []
                else:
                    rooms = catalog(defs,parents,geometry)
                    skeleton = analyze_hull(shell['cells'],[c for _,c in shell['marks']] + sorted(shell['reserved']))
                    save(args.output/'skeleton.json',skeleton.report())
                    skeleton_diagram(args.output/'skeleton.png',skeleton)
                    if guidance:
                        fuel_rate = math.ceil(1000*max(baseline[-1][leg]['requiredFuel'] for leg in ('route','harbor'))
                                              /baseline[-1]['mass'])
                        programs = [dict(modules=(),anchors=(),catalog=rooms,terrainCost=terrain_cost,fuelRate=fuel_rate)]
                    else:
                        programs,program_status = choose(shell,rooms,geometry,cap,terrain_cost,
                                                          args.seed,args.layout_budget,args.programs,skeleton=skeleton)
            rows,failures = [],Counter()
            region_pressure = Counter()
            program_attempts = 0
            for i,program in enumerate(programs):
                modules = program['modules']
                opts = argparse.Namespace(**vars(args))
                if shell['task']:
                    opts.seed += i*709
                    opts.proposals = max(1,args.proposals//len(programs))
                    opts.finalists = max(1,math.ceil(args.finalists/len(programs)))
                    opts.dock_offset = (i%2)*opts.proposals
                print(f'Player shell, {len(shell["cells"])} cells: {", ".join(modules)}',flush=True)
                feedback = {}
                accepted,rejected = search(opts,None,modules,limits,cap,guidance,temporary,
                                           dict(shell,anchors=program['anchors']),feedback if shell['task'] else None,
                                           skeleton,program)
                rows.extend(accepted)
                failures.update(rejected)
                program_attempts += 1
                if 'closest' in feedback:
                    feedback_history.append(dict(program=i,geometryOnly=feedback['closest']))
                if 'frozenCorridors' in feedback:
                    feedback_history.append(dict(program=i,frozenCorridors=feedback['frozenCorridors']))
                if 'fuelRate' in feedback:
                    feedback_history.append(dict(program=i,nativeFuelRate=feedback['fuelRate']))
                if feedback.get('blockedRegions'):
                    feedback_history.append(dict(program=i,blockedRegions=feedback['blockedRegions']))
                if not accepted and skeleton and i+1 < len(programs):
                    target_tank = feedback.get('tankTarget',0)
                    blocked = Counter((d['blockedModule'],d['blockedRegion'])
                                      for d in feedback.get('blockedRegions',()))
                    region_pressure.update(blocked)
                    following = programs[i+1]
                    repeats_conflict = any(blocked[pair] >= 2
                                           for pair in zip(following['modules'],following['regions']))
                    if repeats_conflict or target_tank > following['estimate']['tank']:
                        revised,stage = choose(shell,rooms,geometry,cap,terrain_cost,opts.seed,
                                                args.layout_budget,1,tank_target=target_tank,skeleton=skeleton,
                                                rejected=[p['decisions'] for p in programs[:i+1]],
                                                region_pressure=region_pressure)
                        feedback_history.append(dict(afterProgram=i,requiredTank=target_tank,status=stage,
                            regionalPressure=[(n,k,v) for (n,k),v in sorted(region_pressure.items())]))
                        if revised:
                            programs[i+1] = revised[0]
                if len(rows) >= args.finalists:
                    break
            if not programs and program_status:
                failures['PROGRAM:'+program_status] += 1
            if proven_failure:
                failures[proven_failure] += 1
            status = ('accepted' if rows else 'budget_exhausted' if any('UNKNOWN' in k for k in failures)
                      else 'proven_infeasible' if proven_failure or set(failures) == {'PACK:NO_DOCK'}
                      else 'no_program_under_estimates' if not programs and program_status == 'INFEASIBLE'
                      else 'final_validation_failed' if shell['task'] and any(
                          not k.startswith(('PACK:','PROGRAM:')) for k in failures)
                      else 'layout_budget_exhausted' if shell['task'] else 'no_candidate')
            selected = []
            modules = shell['modules'] if legacy_shell else []
            if rows:
                progress(args,'Export',f'accepted={len(rows)}')
                row = select_final(rows,True)
                preview = build(args.data,'AssemblyPlayerPreview',layout_override=row['layout'],output=args.output)
                save(args.output/'AssemblyPlayerPreview.motion.json',row['native'])
                if row['layout'].fixed_corridor:
                    result = ET.Element('ShipPlanResult',layout='fixed-corridor')
                    rim = ET.SubElement(result,'Rim')
                    for x,z in preview['rimCutouts']:
                        ET.SubElement(rim,'Cell',x=str(x),z=str(z))
                    ET.ElementTree(result).write(args.output/'result.shipplan',encoding='utf-8',xml_declaration=True)
                modules = [p.module for p in row['layout'].packed['placements']]
                route = row['layout'].route
                if skeleton:
                    unused = {tuple(c) for c in row['deck']['unused']}
                    skeleton_diagram(args.output/'layout-uses.png',skeleton,route,row['layout'].packed['placements'],unused)
                    row['deck']['regions'] = [dict(region=i,unused=sum(skeleton.regions[c] == i for c in unused))
                                              for i in skeleton.capacity]
                selected = [dict(name='AssemblyPlayerPreview',topology=route.topology,
                                 sections=route.sections,spine=sorted(route.spine),
                                 **{k:row[k] for k in ('task','deck','resources','propulsionPlan') if k in row})]
            save(args.output/'batch.json',dict(status=status,rejections=dict(failures),modules=modules,
                 seed=args.seed, distance=args.distance, harborDistance=args.harbor_distance,
                 budget=dict(proposals=args.proposals,programs=args.programs,finalists=args.finalists,
                             layoutDeterministicTime=args.layout_budget,engineDeterministicTime=args.engine_budget,
                             engineAttempts=args.engine_attempts),
                 playerShell=sorted(shell['cells']),corridorWidths=shell['widths'],
                 reserveArmourBand=bool(skeleton and guidance and not args.late_propulsion),
                 task=shell['task'],crewCount=shell['crewCount'],materials=cap,skeletonGuide=guidance if skeleton else None,
                 deckLimits=dict(maxUnusedPercent=shell['maxUnused'],maxUnusedBlock=shell['maxUnusedBlock']),
                 programs=[{k:v for k,v in p.items() if k not in ('catalog','terrainCost')} for p in programs],
                 programAttempts=program_attempts,programStatus=program_status,
                 feedback=feedback_history,selected=selected))
            if not rows:
                if status == 'budget_exhausted':
                    stage = 'budget'
                elif any(k.startswith(('PROPULSION:','AUTHORITY:','RESIDUAL:','INSTALL:')) or
                         k == 'PROPULSION_FUEL' for k in failures):
                    stage = 'propulsion'
                elif any(not k.startswith('PACK:') for k in failures):
                    stage = 'layout'
                else:
                    stage = 'packing'
                ET.ElementTree(ET.Element('ShipPlanResult',failure=stage)).write(args.output/'result.shipplan')
            progress(args,'WorkerDone',f'status={status}')
            return 0 if rows else 1
        batch = dict(seed=args.seed,role=role,modules=modules,weights=args.weights,materials=cap,
                     limits=dict(layoutDeterministicTime=args.layout_budget,engineDeterministicTime=args.engine_budget,
                                 proposals=args.proposals,engineAttempts=args.engine_attempts,finalists=args.finalists),
                     baseline=baseline,selected=[],profiles=[])
        paths = args.blueprint or DEFAULTS
        names = [''.join(c for c in p.stem if c.isascii() and c.isalnum()) for p in paths]
        if len(set(names)) != len(names) or any(not n or n == 'Blind' or n[-1].isdigit() for n in names):
            parser.error('blueprint names must give distinct nonnumeric prefab names; reserve Blind for the control')
        cases = [(p,True) for p in paths]
        if args.blind_control:
            cases.append((paths[0],False))
        for p,guided in cases:
            print(f'{p.name} {"guided" if guided else "blind"}:',flush=True)
            opts = argparse.Namespace(**vars(args))
            opts.require_junction = guided and (args.require_junction or
                                     (args.blueprint is None and p.stem == 'Ebon_Hawk'))
            rows,failures = search(opts,p,modules,limits,cap,guided,temporary)
            profile = dict(blueprint=p.name,sourceSha256=sha256(p.read_bytes()).hexdigest(),
                           guided=guided,feasible=len(rows),rejections=dict(failures))
            batch['profiles'].append(profile)
            if not rows:
                print(f'  no accepted candidate: {dict(failures)}',flush=True)
                save(args.output/'batch.json',batch)
                continue
            # Keep the retained chart and report at the same frozen placement.
            row = select_final(rows,guided)
            name = 'AssemblyGenerated'+(''.join(c for c in p.stem if c.isascii() and c.isalnum()) if guided else 'Blind')
            layout = row.pop('layout')
            report = build(args.data,name,layout_override=layout,output=args.output)
            native = row.pop('native')
            save(args.output/f'{name}.motion.json',native)
            batch['selected'].append(dict(name=name,blueprint=p.name,guided=guided,topology=layout.route.topology,
                foundation=len(report['foundation']),unpaired=appearance(layout)[0],**row))
            save(args.output/'batch.json',batch)
        if args.batch_seeds:
            stats = {}
            for seed in range(args.seed,args.seed+args.batch_seeds):
                sample_role,sample_modules = manifest(seed,args.weights)
                slot = stats.setdefault(sample_role,dict(drawn=0,tried=0,passed=0,failures={}))
                slot['drawn'] += 1
                if slot['tried'] == args.batch_solves:
                    continue
                slot['tried'] += 1
                if seed == args.seed:
                    profile = batch['profiles'][0]
                    slot['passed'] += int(profile['feasible'] > 0)
                    slot['failures'] = profile['rejections']
                    continue
                opts = argparse.Namespace(**vars(args))
                opts.seed,opts.finalists = seed,1
                print(f'  batch seed {seed}: {sample_role}',flush=True)
                rows,failures = search(opts,paths[0],sample_modules,limits,cap,True,temporary)
                slot['passed'] += int(bool(rows))
                slot['failures'] = dict(Counter(slot['failures'])+failures)
            batch['sampleByRole'] = stats
        compare_previews(args.output,batch)
        save(args.output/'batch.json',batch)
    return int(len(batch['selected']) != len(cases))


if __name__ == '__main__':
    raise SystemExit(cli())
