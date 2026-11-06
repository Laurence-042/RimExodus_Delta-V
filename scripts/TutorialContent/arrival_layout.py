"""Read the player's intact ship and derive its story wreck without changing the source."""
from pathlib import Path
from collections import Counter, deque
import copy
import json
import re
import hashlib
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(__file__).with_name('arrival_intact.prefab')
DEFS, PARENTS = {}, {}


def load_defs(data):
    DEFS.clear(); PARENTS.clear()
    for path in [*data.glob('*/Defs/**/*.xml'), *(ROOT/'1.6/Defs').glob('**/*.xml')]:
        for node in ET.parse(path).getroot():
            if node.tag != 'ThingDef': continue
            if node.get('Name'): PARENTS[node.get('Name')] = node
            if node.findtext('defName'): DEFS[node.findtext('defName')] = node


def prop(name, xpath, default=None):
    node = DEFS[name]
    while node is not None:
        value = node.findtext(xpath)
        if value is not None: return value
        node = PARENTS.get(node.get('ParentName'))
    return default


def numbers(value): return tuple(map(int, re.findall(r'-?\d+', value)))
def pos(value):
    x,_,z = numbers(value)
    return x,z
def adj(c):
    x,z=c
    return ((x-1,z),(x+1,z),(x,z-1),(x,z+1))


def record(entry):
    name = entry.findtext('def'); root = pos(entry.findtext('pos'))
    rot = int(entry.findtext('rotation','0')); size = numbers(prop(name,'size','(1,1)'))
    w,h = size if rot%2==0 else size[::-1]
    dx,dz = ((0,0),(0,-1),(-1,-1),(-1,0))[rot]
    x = root[0] + (dx if w%2==0 else 0) - (w-1)//2
    z = root[1] + (dz if h%2==0 else 0) - (h-1)//2
    ix,_,iz = numbers(prop(name,'interactionCellOffset','(0,0,0)'))
    ix,iz = ((ix,iz),(iz,-ix),(-ix,-iz),(-iz,ix))[rot]
    return dict(name=name,root=root,rotation=rot,size=size,
                cells={(a,b) for a in range(x,x+w) for b in range(z,z+h)},
                barrier=name=='Door' or float(prop(name,'fillPercent','0'))==1 and prop(name,'passability')=='Impassable',
                interaction=(root[0]+ix,root[1]+iz) if prop(name,'hasInteractionCell','false').lower()=='true' else None,
                entry=entry)


def floors(doc):
    result={}
    for terrain in doc.find('prefab/terrain'):
        for node in terrain.findall('rects/li'):
            x0,z0,x1,z1=numbers(node.text)
            for x in range(x0,x1+1):
                for z in range(z0,z1+1): result[x,z]=terrain.tag
    return result


def components(cells):
    remaining=set(cells); result=[]
    while remaining:
        seed=min(remaining); room={seed}; remaining.remove(seed); queue=deque([seed])
        while queue:
            for c in adj(queue.popleft()):
                if c in remaining: remaining.remove(c);room.add(c);queue.append(c)
        result.append(room)
    return result


def draw(doc, path, title, cargo=None):
    terrain=floors(doc); rows=[record(e) for e in doc.findall('thingStates/li')]
    width,height=numbers(doc.findtext('prefab/size')); scale,pad=22,45
    im=Image.new('RGB',(width*scale+pad*2+310,height*scale+pad*2),'#101a26'); d=ImageDraw.Draw(im)
    font=lambda n: ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',n)
    def box(c):
        x,z=c;return (pad+x*scale,pad+(height-1-z)*scale,pad+(x+1)*scale,pad+(height-z)*scale)
    for c,name in terrain.items():
        if name=='Space':continue
        d.rectangle(box(c),fill='#375a57' if cargo and c in cargo else '#53616a' if name=='SterileTile' else '#354552',outline='#40515e')
    short={'Ship_Reactor':'反应堆','Ship_Beam':'主梁','Ship_CryptosleepCasket':'眠','Ship_ComputerCore':'核心',
           'Ship_SensorCluster':'传感','Building_OutfitStand':'衣','Shelf':'架','ShelfSmall':'架','Battery':'电',
           'UnstablePowerCell':'源','OxygenPump':'氧','Heater':'暖','WallLamp':'灯','Vent':'风','HospitalBed':'医',
           'Bed':'床','DoubleBed':'床','CommsConsole':'讯','ChunkSlagSteel':'渣','Table2x4c':'桌',
           'MealSurvivalPack':'食','MedicineIndustrial':'药'}
    colors={'Wall':'#82939b','Door':'#dcb877','Ship_Reactor':'#c7ad65','Ship_Beam':'#b58c62',
            'Ship_CryptosleepCasket':'#76cab9','OxygenPump':'#73c4e2','Heater':'#da936f','ChunkSlagSteel':'#746d66'}
    for r in rows:
        if r['name']=='HiddenConduit':continue
        xs,zs=zip(*r['cells']);a,b=box((min(xs),max(zs))),box((max(xs),min(zs)))
        d.rectangle((a[0]+1,a[1]+1,b[2]-1,b[3]-1),fill=colors.get(r['name'],'#b6aa98'))
        if r['name'] in short:d.text((a[0]+2,a[1]+1),short[r['name']],font=font(13),fill='#12202c')
    d.text((pad,7),title,font=font(22),fill='white')
    lx=pad+width*scale+20
    counts=Counter(r['name'] for r in rows)
    for i,(name,count) in enumerate(counts.items()):
        d.text((lx,55+i*26),f'{short.get(name,name)} × {count}',font=font(13),fill='#d2dee7')
    for x in range(0,width,5):d.text((pad+x*scale,pad+height*scale+8),str(x),font=font(13),fill='#aabcca')
    for z in range(0,height,5):d.text((12,pad+(height-1-z)*scale),str(z),font=font(13),fill='#aabcca')
    tmp=path.with_suffix('.tmp.png');im.save(tmp);tmp.replace(path)


def field(node,key,value):
    child=node.find(key)
    if child is None:child=ET.SubElement(node,key)
    child.attrib.clear();child.text=str(value)


def relocate(entry, root, rotation=None):
    old=pos(entry.findtext('pos')); state=entry.find('state/saveable')
    saved=pos(state.findtext('pos',entry.findtext('pos')))
    field(entry,'pos',f'({root[0]}, 0, {root[1]})')
    field(state,'pos',f'({saved[0]+root[0]-old[0]}, 0, {saved[1]+root[1]-old[1]})')
    if rotation is not None:
        field(entry,'rotation',rotation);field(state,'rot',rotation)


def build_wreck():
    source_hash=hashlib.sha256(SOURCE.read_bytes()).hexdigest()
    original=ET.parse(SOURCE).getroot(); doc=copy.deepcopy(original)
    rows=[record(e) for e in doc.findall('thingStates/li')]
    terrain=floors(doc); floor={c for c,t in terrain.items() if t!='Space'}
    blocked={c for r in rows if r['barrier'] for c in r['cells']}
    cargo=next(room for room in components(floor-blocked) if (28,35) in room)
    # The blast crosses the aft power section. The surviving forward reactor,
    # all crew caskets and the cargo compartment remain on the same section.
    def fracture_edges(x):
        lower = 11 if x < 10 else 12 if x < 15 else 11 if x < 18 else 10 if x < 23 else 12 if x < 27 else 11
        upper = 17 if x < 9 or x > 33 else 16 if x < 12 or 18 <= x <= 24 or x > 30 else 15
        return lower,upper
    torn={c for c in floor if fracture_edges(c[0])[0]<c[1]<fracture_edges(c[0])[1]}
    floor-=torn
    things=doc.find('thingStates');things.clear()
    templates={r['name']:copy.deepcopy(r['entry']) for r in rows}
    captain=next(r for r in rows if r['name']=='Ship_CryptosleepCasket' and r['root']==(15,22))
    rack=next(r for r in rows if r['name']=='Building_OutfitStand' and r['root']==(16,21))
    original_caskets=sum(r['name']=='Ship_CryptosleepCasket' for r in rows)
    # Loot valuable machinery, not furnishings or food preparation facilities.
    looted={'HiddenConduit','Battery','UnstablePowerCell','Ship_ComputerCore','Ship_SensorCluster',
            'VitalsMonitor','CommsConsole','FlatscreenTelevision','WallLamp','Heater','OxygenPump'}
    removed=Counter()
    for r in rows:
        name,entry=r['name'],r['entry']
        keep=name not in looted
        if name=='Ship_Reactor':keep=r['root']==(21,19)
        if not keep or r['cells'] & torn:
            removed[name]+=1;continue
        state=entry.find('state/saveable')
        if r is captain:relocate(entry,(29,28),1)
        if r is rack:
            relocate(entry,(28,26),0)
        elif name=='Building_OutfitStand':
            state.find('innerContainer/innerList').clear()
        if name=='Ship_Reactor':field(state,'hibernateState','Running')
        if name=='Vent' and any(n in cargo for c in r['cells'] for n in adj(c)):
            field(state,'switchOn','False');field(state,'wantSwitchOn','False')
        if name=='Door':
            field(state,'holdOpen','False');field(state,'open','False')
        if name=='Wall' and min(z for _,z in r['cells'])<=19:
            health=70+(r['root'][0]*29+r['root'][1]*11)%150
            field(entry,'hitPoints',health);field(state,'health',health)
        things.append(entry)

    next_id=950000
    def add(name,root,rotation=0,overrides=None,count=1):
        nonlocal next_id
        if name in templates:entry=copy.deepcopy(templates[name])
        else:
            proto=next(s for s in ET.parse(ROOT/'scripts/TutorialContent/shipyard.states').getroot() if s.findtext('def')==name)
            entry=ET.Element('li');field(entry,'def',name)
            ET.SubElement(entry,'state').append(copy.deepcopy(proto))
            field(entry,'pos','(0, 0, 0)');field(entry,'hitPoints',proto.findtext('health','-1'))
        state=entry.find('state/saveable')
        relocate(entry,root,rotation)
        field(state,'id',name+str(next_id));next_id+=1
        field(entry,'stackCount',count);field(state,'stackCount',count)
        for key,value in (overrides or {}).items():field(state,key,value)
        things.append(entry)
        return entry
    # Cargo alone gets heat and air; validate the heater count with native thermal code.
    add('Heater',(31,26),overrides={'targetTemperature':12})
    add('Heater',(31,27),overrides={'targetTemperature':12})
    add('OxygenPump',(32,26),1)
    add('MealSurvivalPack',(29,26),count=10)
    add('MedicineIndustrial',(30,26),count=5)
    # Only the supply run from the survivor reactor to the aid equipment remains.
    placed=[record(e) for e in things]
    transmitting={c for r in placed if r['name'].startswith('Ship_') for c in r['cells']}
    wire={(24,z) for z in range(22,28)} | {(x,27) for x in range(24,32)}
    for c in sorted(wire-transmitting):add('HiddenConduit',c)
    occupied={c for r in placed for c in r['cells']}
    interactions={r['interaction'] for r in placed if r['interaction']}
    rubble=[c for c in sorted(floor,key=lambda c:(abs(c[1]-14),c[0]))
            if c not in occupied and c not in cargo and c not in interactions and 7<=c[1]<=20
            and (c[0]*7+c[1]*13)%5==0]
    for c in rubble[:22]:add('ChunkSlagSteel',c)

    # Connection references are rebuilt by the common prefab pipeline. Removed
    # apparel and old electrical parents must not survive inside deep state.
    ids={n.findtext('id') for e in things for n in e.find('state/saveable').iter() if n.find('def') is not None and n.find('id') is not None}
    for entry in things:
        state=entry.find('state/saveable')
        for tag in ('parentThing','storageGroup'):
            if state.find(tag) is not None:field(state,tag,'null')
        for node in state.iter():
            if node.text and node.text.startswith('Thing_') and node.text[6:] not in ids:
                node.text='null'
    for node in list(doc.find('prefab/terrain')):
        for cell in node.findall('rects/li'):
            x0,z0,x1,z1=numbers(cell.text)
            assert (x0,z0)==(x1,z1), 'source export uses single-cell terrain entries'
            if (x0,z0) in torn:node.tag='Space'
    roofs=doc.find('roofs')
    for entry in list(roofs):
        c=pos(entry.findtext('pos'))
        if c not in floor or c[1]<=18:roofs.remove(entry)
    if doc.find('noRoof') is not None:doc.remove(doc.find('noRoof'))
    environment=doc.find('environment');environment.clear()
    placed=[record(e) for e in things]
    blocked={c for r in placed if r['barrier'] for c in r['cells']}
    rooms=components(floor-blocked)
    current_cargo=next(room for room in rooms if (28,35) in room)
    assert current_cargo==cargo,'the original cargo compartment must stay sealed and unchanged'
    roof_cells={pos(e.findtext('pos')) for e in roofs}
    assert cargo<=roof_cells,'cargo requires a complete roof'
    for samples,temp,vacuum in ((roof_cells-cargo,-75,1),(cargo,12,0)):
        group=ET.SubElement(environment,'li');field(group,'temperature',temp);field(group,'vacuum',vacuum)
        nodes=ET.SubElement(group,'cells')
        for x,z in sorted(samples):ET.SubElement(nodes,'li').text=f'({x}, 0, {z})'
    field(doc,'name','ArrivalStationWreck');field(doc.find('prefab'),'defName','MapPrefab_ArrivalStationWreck')
    # Static geometry, inventory and environmental contracts, before publishing.
    counts=Counter(r['name'] for r in placed)
    assert counts['Ship_CryptosleepCasket']==original_caskets
    assert {e.findtext('state/saveable/id') for e in things if e.findtext('def')=='Ship_CryptosleepCasket'} == {
        e.findtext('state/saveable/id') for e in original.findall('thingStates/li') if e.findtext('def')=='Ship_CryptosleepCasket'}
    assert counts['Ship_Reactor']==1 and counts['Heater']==2 and counts['OxygenPump']==1
    assert not any('Lamp' in n or n in ('Battery','UnstablePowerCell','Ship_ComputerCore','Ship_SensorCluster') for n in counts)
    assert len(doc.findall(".//innerList/li[def='Apparel_Vacsuit']"))==1
    assert len(doc.findall(".//innerList/li[def='Apparel_VacsuitHelmet']"))==1
    assert len(rack['entry'].find('state/saveable/innerContainer/innerList'))==2
    for name in ('HospitalBed','Bed','DoubleBed','Building_OutfitStand','DiningChair','Table2x4c',
                 'ElectricStove','NutrientPasteDispenser','Hopper'):
        assert counts[name]==sum(r['name']==name for r in rows),('preserved original furnishings',name)
    assert all(r['cells']<=floor for r in placed)
    solid=set()
    for r in placed:
        if r['name']=='HiddenConduit':continue
        assert not solid&r['cells'],('overlap',r['name'],solid&r['cells'])
        solid|=r['cells']
    assert all(r['root'] in cargo for r in placed if r['name'] in ('Heater','OxygenPump','MealSurvivalPack','MedicineIndustrial'))
    assert len(components(floor))==2, 'the blast must separate two deck sections'
    from build import validate_identities
    validate_identities(doc)
    destination=ROOT/'Prefabs/ArrivalStationWreck.xml'
    ET.indent(doc);ET.ElementTree(doc).write(destination,encoding='utf-8',xml_declaration=True)
    draw(doc,destination.with_suffix('.png'),'爆炸与搜刮后的残骸 · 货仓援助区 / 北 ↑',cargo)
    report=dict(source=str(SOURCE),sourceSha256=source_hash,counts=dict(counts),removed=dict(removed),
                cargo=sorted(cargo),roofs=sorted(roof_cells),floor=sorted(floor),temperature=12,
                captain=next(r['root'] for r in placed if r['name']=='Ship_CryptosleepCasket' and r['root'] in cargo),
                records=[{k:sorted(v) if isinstance(v,set) else v for k,v in r.items() if k!='entry'} for r in placed])
    (ROOT/'Prefabs/_design/ArrivalStationWreck.layout.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    # Inputs to the existing offline thermal fixture, kept outside the XML pool.
    thermal=dict(rooms=[dict(name='cargo',cells=sorted(cargo),temperature=12,heaters=2)],
                 outdoorTemperature=-75,generationW=1000,nameplateLoadW=700)
    (ROOT/'Prefabs/_design/ArrivalStation.layout.json').write_text(json.dumps(thermal),encoding='utf-8')
    (ROOT/'Prefabs/_design/ArrivalStation.records.json').write_text(json.dumps(report['records']),encoding='utf-8')
    assert hashlib.sha256(SOURCE.read_bytes()).hexdigest()==source_hash
    print('WRECK',dict(counts),'cargo',len(cargo),'deck sections',[len(c) for c in components(floor)])
    return doc,report,thermal


def build(data):
    load_defs(data)
    (ROOT/'Prefabs/_design').mkdir(parents=True,exist_ok=True)
    doc,report,thermal=build_wreck()
    field(doc,'name','ArrivalStation')
    field(doc.find('prefab'),'defName','MapPrefab_ArrivalStation')
    destination=ROOT/'1.6/Prefabs/ArrivalStation.xml'
    ET.indent(doc);ET.ElementTree(doc).write(destination,encoding='utf-8',xml_declaration=True)
    draw(doc,Path(__file__).with_name('ArrivalStation.png'),'用户原船派生 · 教程残骸 / 北 ↑',set(map(tuple,report['cargo'])))
    generated=ROOT/'Source/obj/TutorialContent';generated.mkdir(parents=True,exist_ok=True)
    (generated/'ArrivalStation.records.json').write_text(json.dumps(report['records'],ensure_ascii=False,indent=2),encoding='utf-8')
    (generated/'ArrivalStation.layout.json').write_text(json.dumps(dict(report,**thermal),ensure_ascii=False,indent=2),encoding='utf-8')
