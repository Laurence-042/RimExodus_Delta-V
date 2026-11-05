"""Approved ring station and gift freighter, authored in local grid coordinates.

build.py resolves every placement through the current Defs and serializes it.
The drawing and checks consume those same records; this module never spawns Things.
"""
import math
import json
from collections import defaultdict, Counter, deque
from PIL import Image, ImageDraw, ImageFont

DIRS=((1,0),(-1,0),(0,1),(0,-1))
NAMES=['公共生活','农业温室','造修工坊','货仓装卸','能源中心','后勤保障','访客接待','乘员居住']
COLORS=['#28545c','#325f49','#665039','#65513a','#574c68','#3b536a','#315265','#405276']
def adj(c): return [(c[0]+x,c[1]+z) for x,z in DIRS]
def components(cells):
    remaining=set(cells); result=[]
    while remaining:
        seed=min(remaining); found={seed}; q=deque([seed]);remaining.remove(seed)
        while q:
            for n in adj(q.popleft()):
                if n in remaining: remaining.remove(n);found.add(n);q.append(n)
        result.append(found)
    return result

def shape(name):
    if name=='PrototypeShip':
        cells={(x,z) for x in range(17) for z in range(25)
               if not(z>=23 and (x<3 or x>13)) and not(21<=z<=22 and (x<1 or x>15))
               and not(z<=2 and (x<2 or x>14))}
        return dict(size=(17,25),cells=cells,zones={c:2 for c in cells},foundationDefs={})
    cells={};zones={}
    for x in range(-50,51):
        for z in range(-50,51):
            r=math.hypot(x,z);a=math.degrees(math.atan2(x,z))%360;i=int((a+22.5)//45)%8
            if 30.5<=r<=50.5:
                offset=(a-i*45+22.5)%360
                sub=min(5,int(offset/7.5)) if i==7 else int(offset/22.5)
                cells[x,z]=f'{i}-{0 if r<37 else 1}-{sub}';zones[x,z]=i
                if 37<=r<=40:cells[x,z]='walk'
            if r<=40 and (abs(x)<=2 or abs(z)<=2):cells[x,z]='walk';zones[x,z]=-1
            if max(abs(x),abs(z))<=10 and abs(x)+abs(z)<=16:
                cells[x,z]='walk' if abs(x)<=1 or abs(z)<=1 else f'core-{int(x>0)}-{int(z>0)}'
                zones[x,z]=-2
    for direction in range(4):
        for u in range(-6,7):
            for v in range(48,57):
                p=[(u,v),(v,u),(u,-v),(-v,u)][direction]
                cells[p]=f'node-{direction}';zones[p]=[0,2,5,6][direction]
                if abs(u)<=2 and v<52:cells[p]='walk'
    for sx,sz in [(-1,1),(1,1),(1,-1),(-1,-1)]:
        for x in range(32,40):
            for z in range(32,40):cells[sx*x,sz*z]=f'defense-{sx}-{sz}';zones[sx*x,sz*z]=-3
    cells={(x+56,z+56):v for (x,z),v in cells.items()}
    zones={(x+56,z+56):v for (x,z),v in zones.items()}
    return dict(size=(113,113),cells=set(cells),groups=cells,zones=zones,foundationDefs={})

def furnish(name,plan,place,prop,foundation,roofs,records,occupied):
    reserved=set(); placements=defaultdict(list)
    def put(n,x,z,rot=0,**kw):
        r=place(n,x,z,rot,**kw);placements[n].append(r)
        if r['hasInteraction']:
            ix,_,iz=r['interaction'];dx,dz=[(ix,iz),(iz,-ix),(-ix,-iz),(-iz,ix)][rot]
            reserved.add((r['root'][0]+dx,r['root'][1]+dz))
        return r
    def dims(n,rot):
        import re
        s=list(map(int,re.findall(r'-?\d+',prop(n,'size','(1,1)'))))
        return s if rot%2==0 else s[::-1]
    def auto(n,area,near=None,rotations=(0,1,2,3),state=None,margin=0):
        area=set(area);near=near or (sum(x for x,z in area)/len(area),sum(z for x,z in area)/len(area))
        candidates=sorted(area,key=lambda c:((c[0]-near[0])**2+(c[1]-near[1])**2,c))
        import re
        offset=list(map(int,re.findall(r'-?\d+',prop(n,'interactionCellOffset','(0,0,0)'))))
        has=prop(n,'hasInteractionCell','false').lower()=='true'
        for x,z in candidates:
            for rot in rotations:
                w,h=dims(n,rot);cells={(x+a,z+b) for a in range(w) for b in range(h)}
                if not cells<=area or cells&occupied.keys() or cells&reserved:continue
                halo={(x+a,z+b) for a in range(-margin,w+margin) for b in range(-margin,h+margin)}
                if not halo<=area:continue
                rx=x+(w-1)//2+(1 if w%2==0 and rot in (2,3) else 0)
                rz=z+(h-1)//2+(1 if h%2==0 and rot in (1,2) else 0)
                ix,_,iz=offset;dx,dz=[(ix,iz),(iz,-ix),(-ix,-iz),(-iz,ix)][rot]
                inter=(rx+dx,rz+dz)
                if has and (inter not in area or inter in occupied or (inter in cells and prop(n,'passability','Standable')=='Impassable')):continue
                # Keep at least one usable maintenance edge for non-walkable machines.
                if not any(c in area and c not in occupied and c not in cells for a in cells for c in adj(a)):continue
                return put(n,x,z,rot,state=state)
        raise ValueError(f'No placement for {n}, area={len(area)}, near={near}')

    if name=='PrototypeShip':
        engines=[]
        for x in (4,11):engines.append(put('LargeThruster',x,0))
        for x in (1,14):engines.append(put('LargeThruster',x,21,2))
        for z in (5,18):
            engines.extend((put('SmallThruster',0,z,1),put('SmallThruster',15,z,3)))
        engine_cells={tuple(c) for r in engines for c in r['cells']};roofs-=engine_cells
        walls={c for c in foundation if any(n not in foundation for n in adj(c))}-engine_cells
        walls|={(x,z) for z in (9,15,20) for x in range(1,16) if x not in (7,8,9)}
        walls|={(x,z) for x in (7,9) for z in range(15,20)}
        ports={(0,14):3,(16,14):1}
        doors={(7,18),(9,18),(4,15),(12,15),(4,9),(12,9)}
        walls-=doors|ports.keys()
        for x,z in sorted(walls):put('GravshipHull',x,z)
        for x,z in sorted(doors):put('Door',x,z)
        for (x,z),rot in ports.items():put('DeltaV_DockingPort',x,z,rot)
        start=(8,14)
        put('PilotConsole',7,22)
        put('Bed',2,16);put('Bed',5,16)
        put('Table2x2c',11,16)
        put('DiningChair',10,16,1);put('DiningChair',13,17,3)
        for x in (2,5,10,13):
            r=put('DeltaV_CargoStorage',x,10,state={'allowedDefs':['Steel','ComponentIndustrial','ComponentSpacer','Chemfuel','MealSurvivalPack','MedicineIndustrial','Silver','RawRice']})
            if x==13:put('DeltaV_CargoTemperatureModule',r['root'][0]+1,r['root'][1]+1,layer='overlay')
        put('GravEngine',7,5)
        put('ChemfuelTank',12,4,state={'fuel':150,'configuredTargetFuelLevel':150})
        put('ChemfuelPoweredGenerator',2,4,state={'fuel':30,'configuredTargetFuelLevel':30})
        put('ChemfuelPoweredGenerator',2,7,state={'fuel':30,'configuredTargetFuelLevel':30})
        put('Battery',11,7);put('Battery',13,7)
        put('OrbitalTradeBeacon',10,13)
        # Supplies use ordinary on-map stacks in the actual ASF storage cells.
        for n,x,z,count in [('Steel',2,10,75),('ComponentIndustrial',2,11,12),('Chemfuel',5,10,150),
                            ('MealSurvivalPack',13,10,10),('MealSurvivalPack',14,10,10),('MealSurvivalPack',14,11,10),
                            ('MedicineIndustrial',13,11,10),('Silver',10,10,100)]:
            put(n,x,z,layer='items',state={'stackCount':count})
        habitable=foundation-walls-engine_cells-set(ports)-doors
    else:
        groups=plan['groups']
        exterior={c for c in foundation if any(n not in foundation for n in adj(c))}
        walls=set(exterior)
        for c,g in groups.items():
            if g=='walk':continue
            if any(n in groups and groups[n]!=g and (groups[n]=='walk' or g>groups[n]) for n in adj(c)):
                walls.add(c)
        ports={(0,56):3,(112,56):1,(56,0):2}
        walls-=ports.keys()
        # Connect enclosed compartments with actual doors. Only interior partition cells may open.
        doors=set();floor=foundation-walls-set(ports)
        while len(parts:=components(floor))>1:
            part_of={c:i for i,part in enumerate(parts) for c in part}
            candidates=[]
            for c in walls-exterior:
                touching={part_of[n] for n in adj(c) if n in part_of}
                if len(touching)>1:
                    priority=0 if any(groups.get(n)=='walk' for n in adj(c)) else 1
                    candidates.append((priority,abs(math.hypot(c[0]-56,c[1]-56)-38.5),c))
            if not candidates:raise ValueError('Partition leaves disconnected rooms with no single-cell door')
            _,_,c=min(candidates);walls.remove(c);doors.add(c);floor.add(c)
        # Every port needs an unobstructed interior approach.
        for (x,z),rot in ports.items():
            dx,dz=[(0,-1),(-1,0),(0,1),(1,0)][rot]
            c=(x+dx,z+dz)
            while c in walls:
                walls.remove(c);doors.add(c);c=(c[0]+dx,c[1]+dz)
        for x,z in sorted(walls):put('GravshipHull',x,z)
        for x,z in sorted(doors):put('Door',x,z)
        for (x,z),rot in ports.items():put('DeltaV_DockingPort',x,z,rot)
        start=(56,56)
        reserved.update(c for c in floor if groups.get(c)=='walk')
        reserved.update(c for door in doors|set(ports) for c in adj(door) if c in foundation and c not in walls)
        areas=defaultdict(set)
        for c,g in groups.items():
            if c not in walls and c not in doors and c not in ports and g!='walk':areas[g].add(c)
        def area(prefix):return {c for g,cells in areas.items() if g.startswith(prefix) for c in cells}
        # Core administration, communications, healthcare and meeting room.
        auto('PilotConsole',area('core-0-1'))
        auto('CommsConsole',area('core-1-1'))
        for _ in range(2):auto('HospitalBed',area('core-0-0'))
        auto('VitalsMonitor',area('core-0-0'))
        auto('Table2x2c',area('core-1-0'))
        for _ in range(4):auto('DiningChair',area('core-1-0'))
        # Twelve double cabins, keeping each bed pair in the same actual compartment.
        for key in sorted(k for k in areas if k.startswith('7-')):
            for _ in range(2):auto('Bed',areas[key])
        # Public dining, kitchen, library and hydroponic decorative garden.
        civic=area('0-')
        for _ in range(6):
            table=auto('Table2x2c',civic)
            tx,tz=table['origin']
            for x,z,rot in [(tx-1,tz,1),(tx+2,tz+1,3),(tx,tz-1,0),(tx+1,tz+2,2)]:
                if (x,z) in civic and (x,z) not in occupied and (x,z) not in reserved:put('DiningChair',x,z,rot)
        for _ in range(2):auto('ElectricStove',civic)
        auto('ChessTable',civic);auto('HoopstoneRing',civic)
        for _ in range(2):auto('Bookcase',civic)
        for _ in range(4):auto('PlantPot',area('node-0'))
        # Each greenhouse gets a grow lamp; basins remain within its conservative lit radius.
        farms=area('1-');lamps=[]
        for key in sorted(k for k in areas if k.startswith('1-')):
            lamp=auto('SunLamp',areas[key]);lamps.append(lamp)
            root=tuple(lamp['root'])
            lit={c for c in areas[key] if math.dist(c,root)<=5.5}
            for _ in range(12):
                try:
                    basin=auto('HydroponicsBasin',lit,rotations=(1,0),state={'plantDefToGrow':'Plant_Rice'})
                    for c in basin['cells']:put('Plant_Rice',*c,layer='plants')
                except ValueError:break
        # Workshop machines and their real operator positions.
        workshop=area('2-')
        for _ in range(2):auto('TableMachining',workshop)
        for n in ('FabricationBench','ElectricTailoringBench','ElectricSmithy','HiTechResearchBench'):auto(n,workshop)
        for _ in range(4):auto('Shelf',workshop)
        cargo=area('3-');containers=[]
        for _ in range(16):containers.append(auto('DeltaV_CargoStorage',cargo,rotations=(0,),state={'allowedDefs':['Steel','Plasteel','ComponentIndustrial','ComponentSpacer','Chemfuel','Silver','MealSurvivalPack','MedicineIndustrial','RawRice','DeltaV_Propellant','DeltaV_SteelHead']}))
        for r in containers[:2]:put('DeltaV_CargoTemperatureModule',r['root'][0]+1,r['root'][1]+1,layer='overlay')
        # Food and maintenance stock are physical stacks, not a replenishment script.
        stock=[('Steel',75)]*16+[('Plasteel',75)]*4+[('ComponentIndustrial',50)]*4+[('ComponentSpacer',10)]*2
        stock += [('Chemfuel',150)]*8+[('MealSurvivalPack',10)]*24+[('MedicineIndustrial',25)]*4+[('Silver',500)]*4
        stock += [('RawRice',75)]*8
        for i,(n,count) in enumerate(stock):
            r=containers[i%len(containers)];cell=r['cells'][(i//len(containers))%len(r['cells'])]
            put(n,*cell,layer='items',state={'stackCount':count})
        support=area('5-')
        for _ in range(4):auto('Bed',support)
        for _ in range(6):auto('Shelf',support)
        for _ in range(2):auto('Table2x2c',area('6-'))
        for _ in range(8):auto('DiningChair',area('6-'))
        auto('HiTechResearchBench',area('6-'));auto('Bookcase',area('6-'))
        # Four defense nodes: one shield, one crewed small gun, two point-defense mounts each.
        for key in sorted(k for k in areas if k.startswith('defense-')):
            a=areas[key]
            auto('DeltaV_ShieldGenerator',a,state={'DeltaV_shieldMode':'Guard','DeltaV_shieldGroundRadius':40,
                 'DeltaV_regularCapacity':750,'DeltaV_autoRejoinPercent':10,'fuel':750,'configuredTargetFuelLevel':750})
            auto('DeltaV_Weapon_RapidMainSmall',a)
            for _ in range(2):auto('DeltaV_Weapon_PDTurret',a)
        moon=area('node-2')
        plan['surfaceDefs']={c:'DeltaV_MoonPool' for c in moon}
        for c in moon:plan['foundationDefs'][c]='DeltaV_MoonPool';roofs.discard(c)
        auto('DeltaV_TractorBeam',moon)
        for _ in range(2):auto('DeltaV_EvaSuit',moon)
        # Crew markers are expanded once by the existing prefab pipeline.
        for prefix,count in [('7-',8),('0-',4),('1-',4),('2-',4),('3-',2),('6-',2)]:
            for _ in range(count):auto('DeltaV_PrefabPawnMarker',area(prefix))
        habitable=foundation-walls-doors-set(ports)
        plan['areas']={k:sorted(v) for k,v in areas.items()}

    # Each enclosed room gets its own pump, heater and light. Equipment is placed before
    # the pumps so all rotations can be checked against the actual mounting wall.
    for room in components(habitable):
        if len(room)<3:continue
        room_reserved=reserved&room
        if room<=reserved:reserved.difference_update(room)
        available=room-occupied.keys()-reserved
        candidates=[]
        for x,z in available:
            for rot,(dx,dz) in enumerate(((0,1),(1,0),(0,-1),(-1,0))):
                if (x+dx,z+dz) in walls:candidates.append((x,z,rot))
        if not candidates:
            # The ring corridor is supplied from a mounting wall beside a compartment.
            raise ValueError(f'No oxygen mounting cell in room of {len(room)} cells, seed={min(room)}')
        x,z,rot=min(candidates);put('OxygenPump',x,z,rot)
        auto('Heater',room,state={'targetTemperature':21})
        auto('StandingLamp',room)
        reserved.update(room_reserved)
    if name=='Shipyard':
        energy={c for c in foundation if plan['zones'].get(c)==4 and c not in walls and c not in doors}
        # Budget covers grow lights, all heaters, all workshops and simultaneous shield recharge.
        demand=sum(max(0,r['powerWatts']) for r in records)+4000
        for _ in range(math.ceil(demand/1200)+6):auto('GravcorePowerCell',energy)
        for _ in range(20):auto('Battery',energy)
    # Full grid under the deck, excluding conducting building footprints.
    transmitting={tuple(c) for r in records if r['transmitsPower'] for c in r['cells']}
    for x,z in sorted(foundation-transmitting):put('HiddenConduit',x,z,layer='conduit')
    plan['walls']=walls;plan['doors']=doors;plan['ports']=ports
    return walls,start

def prepare_state(r,state,field,prop):
    import xml.etree.ElementTree as ET
    name=r['name']
    # Resolve current health and material rather than importing wear from a live sample.
    if r['category']=='Building':
        hp=float(prop(name,'statBases/MaxHitPoints','100'))
        if r['stuff']:
            hp=hp*float(prop(r['stuff'],'stuffProps/statFactors/MaxHitPoints','1'))+float(prop(r['stuff'],'stuffProps/statOffsets/MaxHitPoints','0'))
        elif state.find('stuff') is not None:state.remove(state.find('stuff'))
        field(state,'health',round(hp))
    if name=='DeltaV_Weapon_RapidMainSmall':
        for kind,tag in [('Heads','headSupply'),('Propellant','propellantSupply')]:
            node=ET.SubElement(state,tag)
            capacity=prop(name,"comps/li[@Class='DeltaV.CompProperties_GunSupply'][kind='"+kind+"']/fuelCapacity")
            field(node,'fuel',capacity);field(node,'configuredTargetFuelLevel',capacity);field(node,'allowAutoRefuel','True')
    if r['category']=='Item':
        count=int(state.findtext('stackCount','1'))
        assert 0<count<=int(prop(name,'stackLimit','1')),(name,'invalid stack',count)


def validate_and_draw(name,plan,records,foundation,roofs,reachable,here,generated):
    assert len(components(foundation))==1
    sealed={tuple(c) for r in records if r['airtight'] for c in r['cells']}
    air=foundation-sealed
    assert all(n in foundation for c in air for n in adj(c)),(name,'open hull boundary')
    assert air<=roofs|plan.get('surfaceDefs',{}).keys(),(name,'unroofed interior')
    counts=Counter(r['name'] for r in records)
    for r in records:
        if r['name']=='DeltaV_PrefabPawnMarker':assert tuple(r['root']) in reachable
    if name=='Shipyard':
        assert counts['DeltaV_PrefabPawnMarker']==24
        assert counts['Bed']==28 and counts['DeltaV_CargoStorage']==16
        assert not counts['GravEngine'] and not counts['LargeThruster']
    else:
        assert counts['GravEngine']==1 and counts['Bed']==2 and counts['DeltaV_CargoStorage']==4
        assert not any(r['name'].startswith('DeltaV_Weapon') for r in records)
        engine=next(r for r in records if r['name']=='GravEngine')
        assert len(foundation)<=500 and all(math.dist(c,engine['root'])<=18.9 for c in foundation)
        assert counts['SmallThruster']<=4 and counts['LargeThruster']<=6
    generated_power=-sum(min(0,r['powerWatts']) for r in records)
    load=sum(max(0,r['powerWatts']) for r in records)
    report={'counts':dict(sorted(counts.items())),'foundationCells':len(foundation),'roofCells':len(roofs),
            'generationW':generated_power,'nameplateLoadW':load,'stage':'offline; no game SpawnSetup',
            'foundation':[list(c) for c in sorted(foundation)],'roofs':[list(c) for c in sorted(roofs)]}
    (generated/(name+'.layout.json')).write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    assert generated_power>=load+(4000 if name=='Shipyard' else 0),(name,'power deficit',generated_power,load)
    w,h=plan['size'];s=16 if name=='Shipyard' else 40;pad=80
    im=Image.new('RGB',(w*s+pad*2+620,h*s+pad*2+100),'#101c29');d=ImageDraw.Draw(im)
    def f(n):return ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',n)
    def box(c):x,z=c;return(pad+x*s,pad+(h-1-z)*s,pad+(x+1)*s,pad+(h-z)*s)
    for c in sorted(foundation):
        zone=plan['zones'].get(c,-1);color=COLORS[zone] if zone>=0 else '#3e5360'
        if c in plan['foundationDefs']:color='#397783'
        d.rectangle(box(c),fill=color,outline='#455866')
    colors={'GravshipHull':'#81949f','Door':'#dac397','DeltaV_DockingPort':'#f5d58d',
            'Bed':'#a2b7de','HydroponicsBasin':'#9fd2a2','Plant_Rice':'#80b786','DeltaV_CargoStorage':'#e1b674',
            'GravEngine':'#e5be76','GravcorePowerCell':'#cfb6de','Battery':'#bea0ce',
            'DeltaV_PrefabPawnMarker':'#eddee8','OxygenPump':'#78d1da','SunLamp':'#e6e1a4'}
    for r in records:
        if r['layer'] in ('conduit','items'):continue
        xs,zs=zip(*r['cells']);a=box((min(xs),max(zs)));b=box((max(xs),min(zs)))
        rect=(a[0]+1,a[1]+1,b[2]-1,b[3]-1)
        d.rectangle(rect,fill=colors.get(r['name'],'#c6b6a2'),outline='#182535',width=1)
        if r['name']=='DeltaV_PrefabPawnMarker':d.ellipse(rect,fill='#f5e1e9')
    d.text((pad,18),'星界边境 · 环形船厂' if name=='Shipyard' else '教程赠船 · 无武器货船',font=f(33),fill='#ecf2f5')
    x=w*s+pad+35;y=80
    d.text((x,y),f'{w} × {h} 格  /  北向朝上',font=f(27),fill='#edf0f3');y+=55
    d.text((x,y),'建筑脚印与输出 prefab 同源',font=f(23),fill='#b2c3ce');y+=65
    keynames={'Bed':'床','HospitalBed':'病床','DeltaV_PrefabPawnMarker':'常驻乘员',
              'HydroponicsBasin':'水培盆','SunLamp':'太阳灯','DeltaV_CargoStorage':'货柜',
              'DeltaV_ShieldGenerator':'护盾','DeltaV_Weapon_PDTurret':'点防',
              'DeltaV_Weapon_RapidMainSmall':'小型速射炮','OxygenPump':'供氧泵',
              'GravcorePowerCell':'逆重电芯','Battery':'电池','LargeThruster':'主推进器',
              'SmallThruster':'小推进器','GravEngine':'逆重引擎','ChemfuelPoweredGenerator':'化燃发电机'}
    for key,label in keynames.items():
        if counts[key]:d.text((x,y),f'{label}  {counts[key]}',font=f(24),fill='#d1dce4');y+=38
    y+=20
    for line in [f'发电 {generated_power/1000:.1f} kW',f'铭牌用电 {load/1000:.1f} kW','护盾回充、瞬时开火另计','黄色门：对接 / 通行','浅色圆点：乘员生成位置','蓝绿色地面：月池']:
        d.text((x,y),line,font=f(22),fill='#a7bdcc');y+=36
    if name=='Shipyard':
        for i,angle in enumerate(range(0,360,45)):
            a=math.radians(angle);cx=56+44*math.sin(a);cz=56+44*math.cos(a)
            xy=(pad+(cx+.5)*s,pad+(h-cz-.5)*s)
            t=NAMES[i];bb=d.textbbox(xy,t,font=f(24),anchor='mm');d.rectangle((bb[0]-5,bb[1]-4,bb[2]+5,bb[3]+4),fill='#101c29')
            d.text(xy,t,font=f(24),fill='#f1f3f5',anchor='mm')
    d.text((pad,h*s+pad+25),'已布置建筑、人员标记和补给；游戏内生成、通行及存读档待验收。',font=f(24),fill='#a9bdcd')
    im.save(here/(name+'.png'))
