"""Optional live-fire exhibition; resolved by the shared tutorial prefab builder."""
import xml.etree.ElementTree as ET
import re
import math
from collections import deque
from PIL import Image, ImageDraw

TERMINAL_COLUMNS = (('Unshielded',), ('KineticPassive', 'KineticLow', 'KineticFull'),
                    ('ElectromagneticPassive', 'ElectromagneticHalf', 'ElectromagneticFull'), ('GuardSingle', 'Guard'))


def shape(name):
    size = (35, 43) if name == 'WeaponRange' else (31, 43)
    # A north-pointing floor arrow leads from the berth to the first terminal.
    arrow = {(4,z) for z in range(36,41)} | {(3,39),(5,39),(2,38),(6,38)} if name == 'WeaponRange' else set()
    return dict(size=size, cells={(x,z) for x in range(size[0]) for z in range(size[1])},
                surfaceDefs={c:'GoldTile' for c in arrow})


def furnish(name, plan, place, prop, foundation, roofs, records, occupied):
    if name == 'WeaponRange':
        walls = {(x,z) for x in range(24) for z in range(43) if x in (0,23) or z in (0,42)}
        doors = {(0,39),(7,39),(23,25)}
        # The airtight loader replaces three hull cells on the east wall.
        walls -= {(23,z) for z in range(21,24)}
        walls |= {(x,z) for x in (11,21) for z in (10,20,30)}
        walls |= {(7,z) for z in range(35,42)} | {(x,35) for x in range(8)}
        for x,z in sorted(walls-doors): place('GravshipHull',x,z)
        place('DeltaV_DockingPort',0,39,3)
        place('DeltaV_RangeBlastDoor',7,39,1)
        place('Door',23,25,1)
        roofs.intersection_update({(x,z) for x in range(24) for z in range(43)})
        place('PilotConsole',3,27,1)
        place('DeltaV_RangeTerminal_Introduction',4,41,2)
        for x,column in zip((9,13,17,21),TERMINAL_COLUMNS):
            for z,preset in zip((41,39,37),column):
                place('DeltaV_RangeTerminal_'+preset,x,z,2)
        for x in (3,5): place('Bed',x,7)
        place('Table2x2c',3,32)
        place('DiningChair',2,32,1)
        place('OxygenPump',10,41)
        place('OxygenPump',1,41)
        place('OxygenPump',22,35,1)
        place('Heater',3,14)
        for z in (9,24,37): place('StandingLamp',9,z)
        for x in (12,14,16,18,20):
            for z in range(3,34,3): place('Battery',x,z)
        for x,z in ((3,20),(5,20),(7,20),(9,20),(5,23)): place('Battery',x,z)
        for x in range(3,11):
            for z in (17,18): place('GravcorePowerCell',x,z)
        place('DeltaV_ShieldGenerator',8,29,state={'DeltaV_shieldGroundRadius':'40'})
        for group,(weapon,z) in enumerate((('DeltaV_Weapon_RapidMainMedium',5),
                ('DeltaV_Weapon_HeavyMainMedium',10),('DeltaV_Weapon_LaserMainMedium',15)),1):
            for secondary,origin in ((False,z),(True,41-z)):
                settings={'groupNumber':str(group),'salvoSet':'Secondary' if secondary else 'Main'}
                if group==1: settings.update(firingInterval='10')
                elif group==2: settings.update(propellantCharge='48',headUnits='96' if secondary else '48')
                else: settings.update(pulsePower='3')
                gun=place(weapon,27,origin,1,state=settings)
                slot=list(map(int,re.findall(r'-?\d+',prop(weapon,"comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/attachmentSlots/li/cell"))))
                x0,z0=gun['root'][0]+slot[2],gun['root'][1]-slot[0]
                place('DeltaV_Building_AutoAimPluginII',x0,z0,1,layer='attachment')
        loader=place('DeltaV_Weapon_RailgunLoader',23,21,1,state={'groupNumber':'4'})
        for x in range(24,33): place('DeltaV_Weapon_RailgunAccelerator',x,21,1)
        # The rail lane exits east with no hull, roof or building in front of the final segment.
        assert loader['root'][1] == 22
        start=(1,39)
    else:
        roofs.clear()
        # Six steel layers expose the different penetration budgets before the test equipment.
        walls={(x,z) for x in range(6) for z in range(43)}
        for x,z in sorted(walls): place('Wall',x,z,stuff='Steel')
        for x in (11,15):
            for z in range(2,41): place('Wall',x,z,stuff='Steel')
        # All three domes cross the rail lane before the armour: shared regular
        # capacity plus each struck dome's local emergency reserve can be observed.
        place('DeltaV_ShieldGenerator',25,17,3,state={'DeltaV_shieldGroundRadius':'40',
            'DeltaV_shieldProjection':'32','DeltaV_shieldLength':'10','DeltaV_shieldAngle':'0'})
        # Opposite inclinations place separate upper/lower segments ahead of the armour.
        for z,angle in ((11,-30),(23,30)):
            place('DeltaV_ShieldGenerator',21,z,3,state={'DeltaV_shieldGroundRadius':'38',
                'DeltaV_shieldProjection':'32','DeltaV_shieldLength':'10','DeltaV_shieldAngle':str(angle)})
        place('PilotConsole',25,39)
        place('Battery',28,13)
        # Empty, isolated test batteries avoid electrical explosions masking the narrow penetration path.
        for x in (8,12,16,20,24): place('Battery',x,21,state={'storedPower':'0'})
        for z in range(16,20): place('GravcorePowerCell',22,z)
        start=(24,38)
    transmit={tuple(c) for r in records if r['name'] in ('Battery','GravcorePowerCell') for c in r['cells']}
    # Keep live target power away from expendable devices; the rest of the target remains one grid.
    isolated={(x+dx,z+dz) for r in records if name=='ShieldTarget' and r['name']=='Battery'
              and r['stateOverrides'].get('storedPower')=='0' for x,z in r['cells']
              for dx,dz in ((0,0),(1,0),(-1,0),(0,1),(0,-1))}
    for x,z in sorted(foundation-transmit-isolated): place('HiddenConduit',x,z,layer='conduit')
    return walls,start


def prepare_state(r, state, field, prop):
    name=r['name']
    if name == 'PilotConsole':
        combat=state.find('combat')
        if combat is None: combat=ET.SubElement(state,'combat')
        field(combat,'fireControlMode','Disabled')
    if name == 'Wall':
        base=float(prop(name,'statBases/MaxHitPoints','100'))
        factor=float(prop(r['stuff'],'stuffProps/statFactors/MaxHitPoints','1'))
        offset=float(prop(r['stuff'],'stuffProps/statOffsets/MaxHitPoints','0'))
        field(state,'health',round(base*factor+offset))
    if name.startswith('DeltaV_Weapon_') and 'Accelerator' not in name:
        for obsolete in ('fuel','configuredTargetFuelLevel','allowAutoRefuel','lastMagazineThingId'):
            node=state.find(obsolete)
            if node is not None: state.remove(node)
        for kind,tag in [('Heads','headSupply'),('Propellant','propellantSupply')]:
            capacity=prop(name,"comps/li[@Class='DeltaV.CompProperties_GunSupply'][kind='"+kind+"']/fuelCapacity")
            if capacity:
                node=ET.SubElement(state,tag)
                field(node,'fuel',capacity)
                field(node,'configuredTargetFuelLevel',capacity)
                field(node,'allowAutoRefuel','True')
        coverage=ET.SubElement(state,'combatCoverage')
        field(coverage,'arc','90')
        field(coverage,'range','150')
        field(state,'manualFireDeviationDeg','1')
        field(state,'currentAngle','0')
        gun=state.find('gun')
        if gun is not None:
            gun_def=prop(name,'building/turretGunDef')
            field(gun,'def',gun_def)
            # Retain the nested identity allocated by build.py; the host's numeric
            # ID is already registered even when its Def prefix differs.
            field(gun,'id',gun_def+re.search(r'\d+$',gun.findtext('id')).group())
            verb=gun.find('verbTracker/verbs/li')
            verb.set('Class','DeltaV.Verb_SpaceTurretGround')
            field(verb,'loadID','CompEquippable_'+gun.findtext('id')+'_0')
    if name == 'DeltaV_ShieldGenerator':
        for obsolete in ('DeltaV_guardRecoveryTicksRemaining','DeltaV_guardUnlocked'):
            node=state.find(obsolete)
            if node is not None: state.remove(node)
        field(state,'currentHitPoints','-1')
        field(state,'DeltaV_shieldMode','Guard')
        field(state,'DeltaV_regularCapacity',prop(name,"comps/li[@Class='DeltaV.CompProperties_SpaceShield']/regularCapacity",'750'))
        field(state,'DeltaV_autoRejoinPercent','10')
        field(state,'fuel',prop(name,"comps/li[@Class='CompProperties_Refuelable']/fuelCapacity"))
        field(state,'configuredTargetFuelLevel',prop(name,"comps/li[@Class='CompProperties_Refuelable']/fuelCapacity"))
        field(state,'DeltaV_shieldProjection',r['stateOverrides'].get('DeltaV_shieldProjection','32'))
        field(state,'DeltaV_shieldLength',r['stateOverrides'].get('DeltaV_shieldLength','32'))


def validate_and_draw(name, plan, records, foundation, roofs, reachable, here, generated):
    w,h=plan['size']; scale=19; pad=160 if name=='ShieldTarget' else 35
    img=Image.new('RGB',(w*scale+2*pad,h*scale+2*pad+75),'#101822');d=ImageDraw.Draw(img)
    for x,z in foundation:
        d.rectangle((pad+x*scale,pad+(h-z-1)*scale,pad+(x+1)*scale,pad+(h-z)*scale),
                    fill='#e5bd43' if (x,z) in plan['surfaceDefs'] else '#354653',outline='#415563')
    for r in records:
        if r['layer']=='conduit':continue
        xs,zs=zip(*r['cells']);box=(pad+min(xs)*scale+1,pad+(h-max(zs)-1)*scale+1,pad+(max(xs)+1)*scale-1,pad+(h-min(zs))*scale-1)
        color='#94adbe'
        if r['name'].startswith('DeltaV_Weapon_'):color='#d59758'
        elif 'Shield' in r['name']:color='#72a7e1'
        elif 'Terminal' in r['name']:color='#7ed29d'
        elif r['layer']=='attachment':color='#dfd17c'
        elif r['name'] in ('Wall','GravshipHull'):color='#74868d'
        d.rectangle(box,fill=color)
        if r['name'] not in ('Wall','GravshipHull'):
            label=r['name'].replace('DeltaV_RangeTerminal_','').replace('DeltaV_Weapon_','').replace('DeltaV_','')[:3]
            for column,entries in enumerate(TERMINAL_COLUMNS,1):
                for row,preset in enumerate(entries):
                    if r['name']=='DeltaV_RangeTerminal_'+preset:
                        label=str(column)+('ABC'[row] if len(entries)>1 else '')
            if r['name'].endswith('MainMedium'):
                label=('S' if r['stateOverrides'].get('salvoSet')=='Secondary' else 'M')+r['stateOverrides']['groupNumber']
            d.text((box[0]+1,box[1]+1),label,fill='#101822')
        if r['hasInteraction']:
            ix,_,iz=r['interaction'];dx,dz=[(ix,iz),(iz,-ix),(-ix,-iz),(-iz,ix)][r['rotation']]
            p=(r['root'][0]+dx,r['root'][1]+dz)
            assert p in reachable,(r['name'],'inaccessible station')
        if 'ShieldGenerator' in r['name']:
            radius=float(r['stateOverrides']['DeltaV_shieldGroundRadius'])
            if radius>=40:
                cx,cz=(min(xs)+max(xs)+1)/2,(min(zs)+max(zs)+1)/2
                assert all((x-cx)**2+(z-cz)**2<=radius**2 for x in (0,w) for z in (0,h)), 'guard must cover armour corners'
            if name=='ShieldTarget':
                # Display the stored geometry using the same placement convention as GetSegment;
                # the offline native fixture independently checks the production segments.
                angle=math.radians(90-90*r['rotation']+float(r['stateOverrides']['DeltaV_shieldAngle']))
                nx,nz=math.cos(angle),math.sin(angle)
                distance=float(r['stateOverrides']['DeltaV_shieldProjection'])
                half=float(r['stateOverrides']['DeltaV_shieldLength'])/2
                cx,cz=(min(xs)+max(xs)+1)/2+nx*distance,(min(zs)+max(zs)+1)/2+nz*distance
                pixel=lambda x,z:(pad+x*scale,pad+(h-z)*scale)
                d.line((pixel(cx-nz*half,cz+nx*half),pixel(cx+nz*half,cz-nx*half)),fill='#72a7e1',width=3)
                d.line((pixel(cx,cz),pixel(cx+nx*2,cz+nz*2)),fill='#dfd17c',width=2)
                d.text(pixel(cx-2,cz+1),r['stateOverrides']['DeltaV_shieldAngle']+' deg',fill='white')
    if name=='WeaponRange':
        assert sum(r['name']=='Battery' for r in records)==60
        assert sum(r['name'].startswith('DeltaV_RangeTerminal_') for r in records)==10
        rail=[r for r in records if r['name'] in ('DeltaV_Weapon_RailgunLoader','DeltaV_Weapon_RailgunAccelerator')]
        assert len(rail)==10
        assert {tuple(c) for r in rail for c in r['cells']}=={(x,z) for x in range(23,33) for z in range(21,24)}
        # The berth reaches only the instructions until the single interior blast door unlocks.
        blocked={tuple(c) for r in records if r['name']=='DeltaV_RangeBlastDoor' or
                 (r['passability']=='Impassable' and r['name'] not in ('Door','DeltaV_DockingPort')) for c in r['cells']}
        seen={(1,39)}; queue=deque(seen)
        while queue:
            x,z=queue.popleft()
            for p in ((x-1,z),(x+1,z),(x,z-1),(x,z+1)):
                if p in foundation and p not in blocked and p not in seen:seen.add(p);queue.append(p)
        assert (4,40) in seen and all((x,z-1) not in seen for x,column in zip((9,13,17,21),TERMINAL_COLUMNS)
                                    for z,_ in zip((41,39,37),column))
        assert all(p in seen for p in plan['surfaceDefs']), 'arrow must be visible from the locked vestibule'
    else:
        # Both prefabs have the same height/placement root; the eastward rail axis is local z=22.5.
        assert not any(r['name']=='DeltaV_ShieldGenerator' and any(z==22 for x,z in r['cells']) for r in records)
        assert sum(r['name']=='Battery' and any(z==22 for x,z in r['cells']) for r in records)==5
    d.text((pad,pad+h*scale+12),name+' | North up | FIRE EAST > / TARGET FACES WEST <',fill='white')
    d.text((pad,pad+h*scale+32),'1: off | 2: kinetic 0/20/100 | 3: EM 0/50/100 | 4: guard' if name=='WeaponRange'
           else 'Blue: projected line segments | Yellow: front normals',fill='white')
    d.text((pad,pad+h*scale+52),'Live ammunition, finite energy and real damage. No automatic reset.',fill='white')
    img.save(here/(name+'.png'))
