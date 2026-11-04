"""Optional live-fire exhibition; resolved by the shared tutorial prefab builder."""
import xml.etree.ElementTree as ET
import re
from PIL import Image, ImageDraw

PRESETS = ('Unshielded', 'Kinetic', 'Deflection', 'Reflection', 'GuardSingle', 'Guard')


def shape(name):
    size = (35, 43) if name == 'WeaponRange' else (31, 43)
    return dict(size=size, cells={(x,z) for x in range(size[0]) for z in range(size[1])})


def furnish(name, plan, place, prop, foundation, roofs, records, occupied):
    if name == 'WeaponRange':
        walls = {(x,z) for x in range(24) for z in range(43) if x in (0,23) or z in (0,42)}
        doors = {(0,21),(23,21)}
        walls |= {(x,z) for x in (11,21) for z in (10,20,30,40)}
        for x,z in sorted(walls-doors): place('GravshipHull',x,z)
        place('DeltaV_DockingPort',0,21,3)
        place('Door',23,21,1)
        roofs.intersection_update({(x,z) for x in range(24) for z in range(43)})
        place('PilotConsole',3,27,1)
        for preset,z in zip(PRESETS,(4,10,16,22,28,34)):
            place('DeltaV_RangeTerminal_'+preset,7,z,2)
        for x in (3,5): place('Bed',x,7)
        place('Table2x2c',3,32)
        place('DiningChair',2,32,1)
        place('OxygenPump',10,41)
        place('OxygenPump',22,35,1)
        place('Heater',3,14)
        for z in (9,24,37): place('StandingLamp',9,z)
        for x in (12,14,16,18,20):
            for z in range(3,37,3): place('Battery',x,z)
        for x in range(3,11):
            for z in (38,39): place('GravcorePowerCell',x,z)
        place('DeltaV_ShieldGenerator',8,29,state={'DeltaV_shieldGroundRadius':'40'})
        for group,(weapon,z) in enumerate((('DeltaV_Weapon_RapidMainMedium',6),
                ('DeltaV_Weapon_HeavyMainMedium',14),('DeltaV_Weapon_LaserMainMedium',31)),1):
            gun=place(weapon,27,z,1,state={'groupNumber':str(group)})
            # Current 3x3 turrets have a (-1,0,-1) slot; resolve it from their actual Def.
            slot=list(map(int,re.findall(r'-?\d+',prop(weapon,"comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/attachmentSlots/li/cell"))))
            x0,z0=gun['root'][0]+slot[2],gun['root'][1]-slot[0]
            place('DeltaV_Building_AutoAimPluginII',x0,z0,1,layer='attachment')
        loader=place('DeltaV_Weapon_RailgunLoader',29,21,1,state={'groupNumber':'4'})
        for x in (30,31,32): place('DeltaV_Weapon_RailgunAccelerator',x,21,1)
        # The rail lane exits east with no hull, roof or building in front of the final segment.
        assert loader['root'][1] == 22
        start=(1,21)
    else:
        roofs.clear()
        walls={(x,z) for x in range(7) for z in range(43)}
        for x,z in sorted(walls): place('Wall',x,z,stuff='Plasteel')
        for x in (11,15):
            for z in range(2,41): place('Wall',x,z,stuff='Steel')
        # Only the forward dome covers the armour; rear domes supply the common regular pool.
        place('DeltaV_ShieldGenerator',25,20,3,state={'DeltaV_shieldGroundRadius':'40'})
        for z in (6,34): place('DeltaV_ShieldGenerator',25,z,3,state={'DeltaV_shieldGroundRadius':'8'})
        place('PilotConsole',25,39)
        place('Battery',22,13)
        for z in range(16,20): place('GravcorePowerCell',22,z)
        start=(24,38)
    transmit={tuple(c) for r in records if r['name'] in ('Battery','GravcorePowerCell') for c in r['cells']}
    for x,z in sorted(foundation-transmit): place('HiddenConduit',x,z,layer='conduit')
    return walls,start


def prepare_state(r, state, field, prop):
    name=r['name']
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
        field(coverage,'arc','24')
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
        field(state,'DeltaV_regularCapacity','1000')
        field(state,'fuel','2000')
        field(state,'configuredTargetFuelLevel','2000')
        field(state,'DeltaV_shieldProjection','32')
        field(state,'DeltaV_shieldLength','32')


def validate_and_draw(name, plan, records, foundation, roofs, reachable, here, generated):
    w,h=plan['size']; scale=19; pad=35
    img=Image.new('RGB',(w*scale+2*pad,h*scale+2*pad+75),'#101822');d=ImageDraw.Draw(img)
    for x,z in foundation:
        d.rectangle((pad+x*scale,pad+(h-z-1)*scale,pad+(x+1)*scale,pad+(h-z)*scale),fill='#354653',outline='#415563')
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
    if name=='WeaponRange':
        assert sum(r['name']=='Battery' for r in records)==60
        assert sum(r['name'].startswith('DeltaV_RangeTerminal_') for r in records)==6
        assert sum(r['name']=='DeltaV_Weapon_RailgunAccelerator' for r in records)==3
    d.text((pad,pad+h*scale+12),name+' | North up | FIRE EAST > / TARGET FACES WEST <',fill='white')
    d.text((pad,pad+h*scale+32),'Orange: weapons | Blue: shields | Green: terminals',fill='white')
    d.text((pad,pad+h*scale+52),'Live ammunition, finite energy and real damage. No automatic reset.',fill='white')
    img.save(here/(name+'.png'))
