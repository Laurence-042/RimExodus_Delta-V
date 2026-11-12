"""Run the offline skeleton control, followed by the three Save 56 task cases."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from hashlib import sha256
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw

from module_layout import adjacent


def slender_control(source, output):
    request = ET.parse(source).getroot()
    original = [(int(n.get('x')),int(n.get('z'))) for n in request.findall('./Hull/Cell')]
    count = len(original)
    width = 17
    length,remainder = divmod(count,width)
    if not 1 <= length <= 109:
        raise ValueError('Reference area cannot fit the straight control')
    attrs = dict(request.attrib)
    control = ET.Element('ShipPlan',attrs)
    hull = ET.SubElement(control,'Hull')
    trim = ET.SubElement(control,'Trimmable')
    cells = {(x,z) for x in range(width) for z in range(length)}
    cells.update((x,length) for x in range((width-remainder)//2,(width+remainder)//2))
    x0,z0 = map(min,zip(*original))
    x1,z1 = map(max,zip(*original))
    occupied = set()
    for tag in ('Modules','Reserved','DeckReserve'):
        markers = request.findall(f'./{tag}/Cell')
        if not markers:
            continue
        layer = ET.SubElement(control,tag)
        for marker in markers:
            a = round((int(marker.get('x'))-x0)*(width-1)/max(1,x1-x0))
            b = round((int(marker.get('z'))-z0)*(length-1)/max(1,z1-z0))
            x,z = min(cells-occupied,key=lambda c:(abs(c[0]-a)+abs(c[1]-b),c))
            occupied.add((x,z))
            ET.SubElement(layer,'Cell',dict(marker.attrib,x=str(x),z=str(z)))
    for x,z in sorted(cells):
        ET.SubElement(hull,'Cell',x=str(x),z=str(z))
        if (x,z) not in occupied:
            ET.SubElement(trim,'Cell',x=str(x),z=str(z))
    if (materials := request.find('Materials')) is not None:
        control.append(materials)
    ET.ElementTree(control).write(output,encoding='utf-8',xml_declaration=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('request',type=Path)
    parser.add_argument('output',type=Path)
    parser.add_argument('--jobs',type=int,default=4)
    parser.add_argument('--baseline',type=Path,help='legacy replay directory for mission comparison')
    parser.add_argument('--controls-only',action='store_true')
    args, extra = parser.parse_known_args()
    args.output.mkdir(parents=True,exist_ok=True)
    straight = args.output/'Slender.shipplan'
    slender_control(args.request,straight)
    generator = Path(__file__).with_name('generate_ships.py')

    def run(case):
        label,hull,task,guided = case
        directory = args.output/label
        directory.mkdir(exist_ok=False)
        command = [sys.executable,'-X','utf8',str(generator),'--shell-request',str(hull),
                   '--shell-task',task,'--output',str(directory),'--trace',*extra]
        if not guided:
            command.append('--no-skeleton-guide')
        with (directory/'search.log').open('wb') as log:
            process = subprocess.run(command,stdout=log,stderr=subprocess.STDOUT)
        batch_path = directory/'batch.json'
        if not batch_path.is_file() or process.returncode not in (0,1):
            return dict(case=label,status='worker_error',exitCode=process.returncode)
        report = json.loads(batch_path.read_text(encoding='utf-8'))
        selected = report['selected'][0] if report['selected'] else None
        return dict(case=label,status=report['status'],rejections=report['rejections'],
                    modules=report['modules'],selected=selected,attempts=report['feedback'])

    summary = dict(referenceSha256=sha256(args.request.read_bytes()).hexdigest(),arguments=extra)
    def phase(key,cases):
        with ThreadPoolExecutor(max_workers=args.jobs) as pool:
            summary[key] = list(pool.map(run,cases))
        (args.output/'comparison.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
        print(key,[(r['case'],r['status']) for r in summary[key]],flush=True)
    phase('controls',[(f'{label}-{mode}',hull,'transport',mode == 'guided')
                       for label,hull in (('Save56',args.request),('Slender',straight))
                       for mode in ('guided','control')])
    tiles = []
    for row in summary['controls']:
        directory = args.output/row['case']
        source = directory/'layout-uses.png'
        if not source.is_file():
            proposals = [f for f in row.get('attempts',()) if 'geometryOnly' in f]
            if proposals:
                best = min(proposals,key=lambda f:f['geometryOnly']['estimatedUnused'])
                seed = json.loads((directory/'batch.json').read_text(encoding='utf-8'))['seed']
                source = directory/f"layout-attempt-{seed+709*best['program']}.png"
            if not source.is_file():
                source = directory/'skeleton.png'
        if source.is_file():
            with Image.open(source) as image:
                tiles.append((row,image.convert('RGB')))
    if tiles:
        w,h = max(i.width for _,i in tiles),max(i.height for _,i in tiles)
        canvas = Image.new('RGB',(2*w,2*(h+24)),'#141c25')
        draw = ImageDraw.Draw(canvas)
        for k,(row,image) in enumerate(tiles):
            x,y = k%2*w,k//2*(h+24)
            draw.text((x+10,y+5),row['case']+' | '+row['status'],fill='white')
            canvas.paste(image,(x,y+24))
        canvas.save(args.output/'controls.png')
    def branch_intersections(row):
        cells = {tuple(c) for c in row['selected']['spine']}
        return sum(len(adjacent({c}) & cells) >= 3 for c in cells)
    controls = summary['controls']
    control_checks = False
    h,_,thin,_ = controls
    if h.get('selected') and thin.get('selected'):
        line = {tuple(c) for c in thin['selected']['spine']}
        x,z = zip(*line)
        control_checks = branch_intersections(h) > 0 and max(z)-min(z) >= 2*(max(x)-min(x))
    summary['controlTopologyPassed'] = control_checks
    (args.output/'comparison.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    if args.controls_only:
        return int(not control_checks)
    phase('tasks',[(task,args.request,task,True)
                    for task in ('transport','production','escort')])
    all_tasks = all(row.get('selected') for row in summary['tasks'])
    task_checks = False
    if all_tasks and args.baseline:
        from test_ship_program import accept_previews
        try:
            accept_previews(args.output,args.baseline)
            task_checks = True
        except AssertionError as exc:
            summary['taskCheckFailure'] = str(exc)
    summary.update(taskComparisonPassed=task_checks,
                   note='Both versions of the native fixture must still pass on every selected XML.')
    (args.output/'comparison.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    return int(not (control_checks and task_checks))


if __name__ == '__main__':
    raise SystemExit(main())
