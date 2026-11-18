"""Room programs for exact player hulls; geometry and native flight still decide acceptance."""
from collections import Counter, defaultdict
from dataclasses import dataclass
import math

from ortools.sat.python import cp_model

from build import comp_values, material_cost, thruster_force
from module_layout import MODULES, battery_capacity, furnish, shape
from module_layout import adjacent
from ship_search import (DIR, GALLEY, REFUEL, add_open_seams, elongated_hull, move,
                         room_option_geometry, solver_for, translate, turn)


DIMENSIONS = ('production', 'cargo', 'armament', 'defense')
PRESETS = {
    'balanced': (4, 4, 3, 3),
    'transport': (1, 12, 1, 2),
    'production': (12, 3, 1, 2),
    'escort': (1, 2, 10, 8),
}
PRESET_CREW = {'balanced':2, 'transport':2, 'production':4, 'escort':6}
# Fixed reference units and marginal weights, shared by all candidates.
REFERENCE = (6, 192, 4, 4)
ARMAMENT = {
    'DeltaV_Weapon_HeavyMainMedium': 3, 'DeltaV_Weapon_RapidMainMedium': 2,
    'DeltaV_Weapon_LaserMainMedium': 2,
}
PRODUCTION = {'TableMachining': 1, 'FabricationBench': 3, 'HiTechResearchBench': 2}
DEFENSE = {'DeltaV_Weapon_PDTurret': 1, 'DeltaV_ShieldGenerator': 4}
OPTIONAL = ('ModuleCargo','ModuleMachining','ModuleFabrication','ModuleResearch',
            'ModuleGenerator','ModuleFuel','ModuleBedroom','ModuleReactor',
            'ModuleHeavyBattery','ModuleRapidBattery','ModuleLaserBattery',
            'ModulePointDefense','ModuleGuard')
PREFERRED_LIMITS = dict(ModuleCargo=4,ModuleMachining=4,ModuleFabrication=3,ModuleResearch=2,
                        ModuleGenerator=4,ModuleFuel=3,ModuleBedroom=3,ModuleReactor=1,
                        ModuleHeavyBattery=3,ModuleRapidBattery=3,ModuleLaserBattery=3,
                        ModulePointDefense=3,ModuleGuard=3)
RECHARGE_W = 1000  # Guard continuous recharge; also checked in assembly validation.
BURST_TICKS = 600
WEAPON = "comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/"


def reward(units, reference):
    return sum(max(1, 12 - 3*(i//reference)) for i in range(units)) * 192 // reference


def net_power(names, defs, parents):
    return -sum(float(v) for name in names for v in comp_values(name, 'basePowerConsumption', defs, parents)
                ) - RECHARGE_W*names.count('DeltaV_ShieldGenerator')


def burst_energy(names, prop):
    return sum(float(prop(n,WEAPON+'shotEnergy','0')) *
               math.ceil(BURST_TICKS/max(1,float(prop(n,WEAPON+'cooldownTicks','1'))))
               for n in names if n in ARMAMENT or n in DEFENSE)


@dataclass
class Room:
    area: int
    shared_area: int
    pressure: int
    power: int
    storage: int
    burst: int
    tank: int
    generator_fuel_balance: int
    beds: int
    mass: int
    materials: Counter
    capabilities: tuple


def capabilities(names):
    counts = Counter(names)
    return (sum(counts[n]*v for n,v in PRODUCTION.items()), counts['DeltaV_CargoStorage']*48,
            sum(counts[n]*v for n,v in ARMAMENT.items()), sum(counts[n]*v for n,v in DEFENSE.items()))


def catalog(defs, parents, geometry):
    prop,_,footprint,_,_ = geometry
    rooms = {}
    for name in sorted(MODULES.keys() | {'Galley'}):
        plan = shape(name,GALLEY if name == 'Galley' else None)
        fixtures, occupied = [], {}
        def collect(thing,x,z,r=0,stuff=None,layer='body',state=None):
            if layer != 'body':
                return
            cells = footprint(thing,x,z,r)[2]
            fixtures.append(thing)
            occupied.update({c:dict(name=thing,transmitsPower=False) for c in cells})
        furnish(name,plan,collect,footprint,prop,plan['cells'],plan['cells'],[],occupied)
        materials = Counter()
        for thing in fixtures:
            # Bound only irreducible module costs here. Walls can be shared or removed.
            if thing == 'GravshipHull':
                continue
            materials.update(material_cost(defs[thing],parents))
            if cost := int(prop(thing,'costStuffCount','0')):
                materials['Steel'] += cost
        def fuel(thing, field):
            return float(prop(thing,REFUEL+field,'0'))
        rooms[name] = Room(
            len(plan['cells']), len(plan['cells']-(plan['shell']-plan['doors']-plan['vents'])),
            len(plan['interior']),
            math.floor(net_power(fixtures,defs,parents)),
            int(sum(float(prop(n,battery_capacity[n])) for n in fixtures if n in battery_capacity)),
            math.ceil(burst_energy(fixtures,prop)),
            int(sum(fuel(n,'fuelCapacity') for n in fixtures if n in ('ChemfuelTank','LargeChemfuelTank'))),
            math.floor(sum(fuel(n,'fuelCapacity')-6*fuel(n,'fuelConsumptionRate') for n in fixtures
                           if n in ('ChemfuelPoweredGenerator','DeltaV_ShipReactor'))),
            fixtures.count('Bed'), math.ceil(sum(float(prop(n,'statBases/Mass','4')) for n in fixtures)),
            materials,capabilities(fixtures))
    return rooms


def program_budget(model, shell, rooms, geometry, materials, terrain_cost, qty, upper,
                   corridor_area=None, fuel_rate=None, tank_target=0, propulsion_mass=0):
    names = sorted(qty)
    size = len(shell['cells'])
    forced = Counter(name for name,_ in shell['marks'])
    if corridor_area is None:
        corridor_area = round(size*.15)
    if fuel_rate is None:
        fuel_rate = round(300*shell['distance']/20000)
    def total(field):
        return sum(getattr(rooms[n],field)*qty[n] for n in names)
    model.add(total('shared_area') <= size)
    model.add(sum(qty.values()) <= 24)
    model.add(total('beds') >= shell['crewCount'])
    staffed = [n for n in names if rooms[n].capabilities[2]]
    if staffed:
        model.add(sum(qty[n] for n in staffed) <= max(shell['crewCount']-1,sum(forced[n] for n in staffed)))
    model.add(total('generator_fuel_balance') >= 0)
    prop = geometry[0]
    pump_w = int(float(prop('OxygenPump',"comps/li[@Class='CompProperties_Power']/basePowerConsumption",'100')))
    heater_w = int(float(prop('Heater',"comps/li[@Class='CompProperties_Power']/basePowerConsumption",'175')))
    pressure = total('pressure')
    # Approximate circulation and heat load; final devices come from actual pressure zones.
    pump = model.new_int_var(1,24,'pumps')
    heater = model.new_int_var(1,48,'heaters')
    oxygen_rate = 100 * float(prop('OxygenPump',
        "comps/li[@Class='CompProperties_OxygenPusher']/airPerSecondPerHundredCells"))
    model.add(int(120*oxygen_rate)*pump >= pressure+corridor_area)
    heat = float(prop('Heater',"comps/li[@Class='CompProperties_TempControl']/energyPerSecond"))
    delta = max(0,21-shell['outsideTemp'])
    loss = (min(100,delta)+5*max(0,delta-100))*.003 + .01
    model.add(round(100*heat)*heater >= math.ceil(100*loss)*(pressure+corridor_area))
    if delta > 0:
        # Local compartments round up individually and the corridor needs
        # its own supply; a single thermal budget undercounts both.
        model.add(10*heater >= 12*sum(qty.values()) + 5*qty.get('ModuleBedroom',0))
    model.add(total('power') >= 100+pump_w*pump+heater_w*heater)
    central = int(float(prop('Battery',battery_capacity['Battery'])))
    model.add(total('storage')+central >= 600)
    model.add(total('storage')+central >= total('burst'))
    # Search with a mass-scaled fuel estimate calibrated against the native
    # allocator. Actual loaded and reserve requirements are checked after flight.
    dry_mass = size + total('mass') + propulsion_mass
    holds = sum(rooms[n].capabilities[1]//48*qty[n] for n in names)
    nominal_mass = dry_mass + 1800*holds
    model.add(1000*total('tank') >= fuel_rate*nominal_mass)
    if tank_target:
        model.add(total('tank') >= tank_target)
    for m,cap in sorted((materials or {}).items()):
        min_hull = size-min(60,size//12)
        model.add(sum(rooms[n].materials[m]*qty[n] for n in names)+min_hull*terrain_cost[m] <= cap)
    scores = []
    for i,ref in enumerate(REFERENCE):
        cap = sum(rooms[n].capabilities[i]*upper[n] for n in names)
        units = model.new_int_var(0,cap,DIMENSIONS[i])
        demand = sum(rooms[n].capabilities[i]*qty[n] for n in names)
        if DIMENSIONS[i] == 'armament':
            model.add(units <= demand)
            model.add(units <= 3*(shell['crewCount']-1))
        else:
            model.add(units == demand)
        bands = [max(0,min(ref,cap-i*ref)) for i in range(4)] + [max(0,cap-4*ref)]
        parts = [model.new_int_var(0,width,f'{DIMENSIONS[i]}Band{j}') for j,width in enumerate(bands)]
        model.add(sum(parts) == units)
        scores.append(sum(part*(w*192//ref)
                          for part,w in zip(parts,(12,9,6,3,1)))*shell['weights'][i])
    return scores,dict(pressure=pressure,pumps=pump,heaters=heater,tank=total('tank'),
                       dryMass=dry_mass,typicalMass=dry_mass+450*holds,
                       loadedMass=nominal_mass,powerMarginW=total('power')-pump_w*pump-heater_w*heater,
                       connectedStorageWd=total('storage')+central)


def choose(shell, rooms, geometry, materials, terrain_cost, seed, budget, count, tank_target=0,
           skeleton=None, rejected=(), region_pressure=None):
    """Enumerate promising programs. Exclusion here visits a different manifest,
    it does not prove that a previous manifest has no spatial solution."""
    size = len(shell['cells'])
    forced = Counter(name for name,_ in shell['marks'])
    required = forced | Counter(ModuleBridge=1,Galley=1)
    if shell['task'] in ('balanced','transport'):
        required['ModuleCargo'] = max(1,required['ModuleCargo'])
    names = sorted(set(OPTIONAL) | required.keys())
    model = cp_model.CpModel()
    # Keep the optimistic area bound separate from the preferred planning density.
    area_goal = round(size * .64)
    upper = {n:min(max(PREFERRED_LIMITS[n],required[n]),size//rooms[n].shared_area)
             if n in OPTIONAL else required[n] for n in names}
    if any(required[n] > upper[n] for n in names):
        return [], 'INFEASIBLE'
    qty = {n:model.new_int_var(required[n],upper[n],n) for n in names}
    allocation, marked, widths, region_cost = {}, [], {}, []
    if skeleton:
        hull = frozenset(shell['cells'])
        rotate_bridge = elongated_hull(hull)
        occupancy, typical, demand = defaultdict(list), defaultdict(list), defaultdict(list)
        def register(k,options,v):
            areas = sorted(sum(skeleton.regions[c] == k for c in p.cells-p.wall) for p in options)
            occupancy[k].append(areas[0]*v)
            typical[k].append(areas[len(areas)//2]*v)
            demand[k].append(v)
        for n in names:
            groups = defaultdict(list)
            for p in room_option_geometry(n,hull,None,rotate_bridge):
                groups[skeleton.region(p.cells-p.wall)].append(p)
            for k,options in sorted(groups.items()):
                v = model.new_int_var(0,upper[n]-forced[n],f'{n}@{k}')
                allocation[n,k] = v
                register(k,options,v)
        for n,c in sorted(shell['marks']):
            groups = defaultdict(list)
            for p in room_option_geometry(n,hull,c,rotate_bridge):
                groups[skeleton.region(p.cells-p.wall)].append(p)
            row = {}
            for k,options in sorted(groups.items()):
                v = model.new_bool_var(f'{n}@{c}:{k}')
                row[k] = v
                register(k,options,v)
            model.add_exactly_one(list(row.values()))
            marked.append((n,c,row))
        for n in names:
            model.add(qty[n] == forced[n]+sum(v for (m,_),v in allocation.items() if m == n))
        for k,cap in skeleton.capacity.items():
            feasible = sorted(set(shell['widths']) & set(skeleton.widths[k]))
            # A region can also be served from a neighboring segment; a zero
            # width here leaves actual door connectivity to the grid solver.
            width = widths[k] = model.new_int_var_from_domain(
                cp_model.Domain.from_values([0]+feasible),f'width@{k}')
            served = sum(demand[k])
            model.add(sum(occupancy[k])+width <= cap)
            if feasible:
                active = model.new_bool_var(f'branch@{k}')
                model.add(served >= 1).only_enforce_if(active)
                model.add(served == 0).only_enforce_if(active.Not())
                model.add(width >= 1).only_enforce_if(active)
                model.add(width == 0).only_enforce_if(active.Not())
                desired = model.new_int_var(0,3,f'widthGoal@{k}')
                model.add_min_equality(desired,[3,served])
                gap = model.new_int_var(0,3,f'widthGap@{k}')
                model.add_abs_equality(gap,width-desired)
                region_cost.append(35*gap)
            gap = model.new_int_var(0,size*4,f'areaGap@{k}')
            model.add_abs_equality(gap,sum(typical[k])-round(cap*.51))
            region_cost.append(100*gap)
        for (n,k),attempts in sorted((region_pressure or {}).items()):
            if (n,k) in allocation:
                region_cost.append(1500*min(attempts,3)*allocation[n,k])
    area = sum(rooms[n].area*qty[n] for n in names)
    scores,estimate = program_budget(model,shell,rooms,geometry,materials,terrain_cost,qty,upper,tank_target=tank_target)
    pump,heater,nominal_mass = (estimate[k] for k in ('pumps','heaters','loadedMass'))
    # Coverage is a search heuristic, never evidence of final deck utilization.
    area_gap = model.new_int_var(0,size*2,'room area gap')
    model.add_abs_equality(area_gap,area-area_goal)
    model.maximize(sum(scores) - 540*area_gap - 10*(pump+heater) - nominal_mass - sum(region_cost))
    decisions = list(allocation.values())+[v for _,_,row in marked for v in row.values()]+list(widths.values())
    for previous in rejected:
        if decisions and len(previous) == len(decisions):
            model.add_forbidden_assignments(decisions,[previous])
    programs, status = [], 'UNKNOWN'
    for k in range(count):
        solver = solver_for(seed+k,budget)
        status = solver.status_name(solver.solve(model))
        if status not in ('OPTIMAL','FEASIBLE'):
            break
        values = [solver.value(qty[n]) for n in names]
        decision = [solver.value(v) for v in decisions]
        model.add_forbidden_assignments(decisions or list(qty.values()),[decision or values])
        if skeleton:
            # Explore different manifests as well as different assignments of
            # one manifest; close alternatives otherwise consume the budget.
            model.add_forbidden_assignments(list(qty.values()),[values])
        # Revisit the trade between cargo and propulsion rather than spending
        # every layout attempt on a one-room variation of the same loaded mass.
        cargo_rooms = solver.value(qty['ModuleCargo'])
        if k % 2 == (1 if skeleton else 0) and cargo_rooms > max(2,required['ModuleCargo']):
            model.add(qty['ModuleCargo'] <= cargo_rooms-1)
        if shell['task'] == 'production' and k % 2 == 0:
            # Reserve search attempts for smaller workshops when the
            # highest-scoring collection of fabrication rooms cannot fit.
            fabrication = solver.value(qty['ModuleFabrication'])
            if fabrication > max(1,forced['ModuleFabrication']):
                model.add(qty['ModuleFabrication'] <= fabrication-1)
        marks = sorted(shell['marks'],key=lambda m:(m[0]!='ModuleBridge',m))
        added = [(n,None) for n,v in zip(names,values) for _ in range(v-forced[n])]
        layout = sorted(marks+added,key=lambda m:(m[0]!='ModuleBridge',m[1] is None,m[0],m[1] or (0,0)))
        if skeleton:
            assigned = [(n,c,next(k for k,v in row.items() if solver.value(v))) for n,c,row in marked]
            assigned.extend((n,None,k) for (n,k),v in allocation.items() for _ in range(solver.value(v)))
            assigned.sort(key=lambda m:(m[0]!='ModuleBridge',m[1] is None,m[0],m[1] or (0,0),m[2]))
            layout = [(n,c) for n,c,_ in assigned]
        programs.append(dict(modules=tuple(n for n,_ in layout),anchors=tuple(c for _,c in layout),
                             rooms=dict(zip(names,values)), status=status,
                             estimate={**{k:solver.value(v) for k,v in estimate.items()},'area':solver.value(area)}))
        if skeleton:
            programs[-1].update(regions=[k for _,_,k in assigned],widthPlan={k:solver.value(v) for k,v in widths.items()},
                                decisions=decision,programBound=solver.best_objective_bound,
                                programObjective=solver.objective_value)
    return programs,status


def place_program(shell, route, rooms, geometry, materials, terrain_cost, seed, budget, count,
                  fuel_rate=None, feedback=None, progress=None, armour_band=None, limits=None):
    """Select room proportions, their exact locations and their seams together."""
    if progress:
        progress('RoomModel',f'corridor={len(route.floor)}')
    required = Counter(name for name,_ in shell['marks']) | Counter(ModuleBridge=1,Galley=1)
    if shell['task'] in ('balanced','transport'):
        required['ModuleCargo'] = max(1,required['ModuleCargo'])
    names = sorted(set(OPTIONAL) | required.keys())
    hull = frozenset(shell['cells']-shell['deckReserved'])
    port_clearance = {move(route.port,DIR[route.rotation],n) for n in range(5)}
    prop,_,footprint,_,_ = geometry
    choices = []
    for name in names:
        local = shape(name,GALLEY if name == 'Galley' else None)
        w,h = local['size']
        fixtures = local['partitions'] | set().union(*(footprint(f[0],f[1],f[2],f[3] if len(f)>3 else 0)[2]
                                                    for f in local['fixtures']))
        options = []
        markers = {c for n,c in shell['marks'] if n == name}
        for p in room_option_geometry(name,hull,None,True):
            if not p.approaches <= route.floor or p.cells & (route.floor | port_clearance):
                continue
            if len(markers & (p.cells-p.wall-p.doors-p.vents)) > 1:
                continue
            blocked = p.wall | p.doors | p.vents
            if shell['reserved'] & p.cells:
                blocked |= translate(turn(fixtures,w,h,p.rot),p.x,p.z)
            if not shell['reserved'] & blocked:
                options.append(p)
        choices.append(options)
    joins = add_open_seams(choices,names,geometry)
    model = cp_model.CpModel()
    rows = [[model.new_bool_var(f'room_{i}_{j}') for j in range(len(options))]
            for i,options in enumerate(choices)]
    upper = {name:min(len(row),max(PREFERRED_LIMITS.get(name,required[name]),required[name]))
             for name,row in zip(names,rows)}
    missing = [name for name in names if upper[name] < required[name]]
    if missing:
        yield dict(status='NO_PORT_SLOTS',missing=missing)
        return
    qty = {name:model.new_int_var(required[name],upper[name],name) for name in names}
    for name,row in zip(names,rows):
        model.add(qty[name] == sum(row))
    for name,anchor in shell['marks']:
        i = names.index(name)
        model.add(sum(v for p,v in zip(choices[i],rows[i])
                      if anchor in p.cells-p.wall-p.doors-p.vents) == 1)
    cells,solid,faces = defaultdict(list),defaultdict(list),defaultdict(list)
    useful, opened, cargo_walk = [],[],[]
    for i,options in enumerate(choices):
        for p,v in zip(options,rows[i]):
            interior = p.cells-p.wall
            for c in p.cells:
                cells[c].append(v)
            for c in interior:
                solid[c].append(v)
            for c in p.wall:
                for r,d in enumerate(DIR):
                    if move(c,d,-1) in interior and move(c,d) not in p.cells:
                        faces[c,r].append(v)
            useful.append(len(interior-p.doors-p.vents)*v)
            if p.cut:
                opened.append(len(p.cut)*v)
            if p.module == 'ModuleCargo':
                cargo_walk.append(min(abs(x-route.port[0])+abs(z-route.port[1])
                                      for x,z in p.approaches)*v)
    for edges,options in joins.items():
        for i,j in options:
            mates = [rows[k][l] for k,l in joins.get(edges[::-1],()) if i != k
                     and adjacent(choices[i][j].join[2]) & choices[k][l].join[2]]
            model.add(rows[i][j] <= sum(mates))
    sharing = []
    for c,uses in sorted(cells.items()):
        model.add_at_most_one(solid[c])
        model.add(sum(uses)+sum(solid[c]) <= 2)
        if len(uses) > 1:
            shared = model.new_bool_var(f'shared_{c}')
            model.add(sum(uses) == 2).only_enforce_if(shared)
            model.add(sum(uses) < 2).only_enforce_if(shared.Not())
            sharing.append(shared)
    for (c,r),here in sorted(faces.items()):
        other = (move(c,DIR[r]),(r+2)%4)
        if (c,r) < other and other in faces:
            model.add_at_most_one(here+faces[other])
    propulsion_mass = 0
    engine_counts = []
    if armour_band is not None:
        small_force = thruster_force(prop, 'SmallThruster')
        force_units = round(abs(small_force))
        engine_counts = [model.new_int_var(0,len(hull),f'engineBudget_{r}') for r in range(4)]
        wall_mass = float(prop('GravshipHull','statBases/Mass','4'))
        mount_mass = max(0,math.ceil(float(prop('SmallThruster','statBases/Mass','4'))-wall_mass))
        cable_mass = math.ceil(float(prop('HiddenConduit','statBases/Mass','4')))
        wired_cells = sum(rooms[name].area*qty[name] for name in names)+len(route.floor)-sum(sharing)
        propulsion_mass = 2*len(route.floor)+cable_mass*wired_cells+mount_mass*sum(engine_counts)
    scores, estimate = program_budget(model,shell,rooms,geometry,materials,terrain_cost,qty,upper,
                                      corridor_area=len(route.floor),fuel_rate=fuel_rate,
                                      propulsion_mass=propulsion_mass)
    if armour_band is not None:
        corridor_walls = adjacent(route.floor)
        by_rotation = [[] for _ in DIR]
        corridor_capacity = [0]*4
        cx = sum(x+.5 for x,_ in hull)/len(hull)
        cz = sum(z+.5 for _,z in hull)/len(hull)
        radius_sq = sum((x+.5-cx)**2+(z+.5-cz)**2 for x,z in hull)/len(hull)
        max_arms = [[0]*4 for _ in range(2)]
        for (kind,r,_),(inner,outer,cut,service) in armour_band.items():
            if kind != 'SmallThruster':
                continue
            face = next(iter(inner))
            if not faces.get((face,(r+2)%4)) and not (face in corridor_walls and service & route.floor):
                continue
            direction = (r + (2 if small_force < 0 else 0)) % 4
            dx,dz = DIR[direction]
            arm = (face[0]+.5-cx)*dz-(face[1]+.5-cz)*dx
            for i,sign in enumerate((1,-1)):
                max_arms[i][direction] = max(max_arms[i][direction],math.ceil(10*sign*arm))
            if face in corridor_walls and service & route.floor:
                corridor_capacity[direction] += 1
            else:
                by_rotation[direction].extend(faces.get((face,(r+2)%4),()))
        for direction,count_at_edge in enumerate(engine_counts):
            model.add(count_at_edge <= sum(by_rotation[direction])+corridor_capacity[direction])
        i = names.index('ModuleBridge')
        for facing in range(4):
            bridge = [v for p,v in zip(choices[i],rows[i]) if p.rot == facing]
            if not bridge:
                continue
            facing_used = model.new_bool_var(f'bridge_facing_{facing}')
            model.add_max_equality(facing_used,bridge)
            for axis,offset in enumerate((1,3,0,2)):
                direction = (facing+offset)%4
                for load,mass in zip(limits,('dryMass','typicalMass','loadedMass')):
                    model.add(1000*force_units*engine_counts[direction] >= math.ceil(1000*load[axis])*estimate[mass]
                              ).only_enforce_if(facing_used)
        for axis,arms in enumerate(max_arms,4):
            torque = force_units*sum(arm*count for arm,count in zip(arms,engine_counts))
            for load,mass in zip(limits,('dryMass','typicalMass','loadedMass')):
                model.add(torque >= math.ceil(10*load[axis]*radius_sq)*estimate[mass])
        estimate.update({f'mounts{r}':value for r,value in enumerate(engine_counts)})
    model.maximize(1000000*sum(useful)+12000*sum(opened)+6000*sum(sharing)+sum(scores)-sum(cargo_walk)-sum(engine_counts))
    for attempt in range(count):
        solver = solver_for(seed+attempt,budget)
        if progress:
            progress('RoomSolve',f'layout={attempt+1}/{count} placements={sum(map(len,rows))} det_budget={budget}')
        status = solver.status_name(solver.solve(model))
        if progress:
            progress('RoomSolve',f'layout={attempt+1}/{count} status={status} '
                     f'wall={solver.wall_time:.3f}s det={solver.response_proto.deterministic_time:.3f}')
        if status not in ('OPTIMAL','FEASIBLE'):
            yield dict(status=status)
            return
        used = [(p,v) for options,row in zip(choices,rows) for p,v in zip(options,row)
                if solver.boolean_value(v)]
        yield dict(status=status,placements=[p for p,_ in used],objective=solver.objective_value,
                   bound=solver.best_objective_bound,
                   estimate={k:solver.value(v) for k,v in estimate.items()})
        observed = (feedback or {}).get('fuelRate',0)
        if observed > (fuel_rate or 0):
            model.add(1000*estimate['tank'] >= observed*estimate['loadedMass'])
            fuel_rate = observed
        selected_ids = {v.Index() for _,v in used}
        model.add_bool_or(v.Not() if v.Index() in selected_ids else v for row in rows for v in row)


def measure(shell, report, records, prop):
    """Report the actual task benefit only after layout, service and flight pass."""
    names = [r['name'] for r in records if r['layer'] == 'body']
    production,cargo,_,_ = capabilities(names)
    active_weapons = sorted((n for n in names if n in ARMAMENT),key=lambda n:-ARMAMENT[n])[:shell['crewCount']-1]
    armament = sum(ARMAMENT[n] for n in active_weapons)
    key_names = ('PilotConsole','DeltaV_ShipReactor','ChemfuelPoweredGenerator','DeltaV_DockingPort')
    critical = {tuple(c) for r in records if r['name'] in key_names for c in r['cells']}
    covered = {c for r in records if r['name'] == 'DeltaV_ShieldGenerator' for c in critical
               if math.dist(c,r['root']) <= float(r['stateOverrides'].get('DeltaV_shieldGroundRadius',24.9))}
    defense = names.count('DeltaV_Weapon_PDTurret') + round(4*len(covered)/max(1,len(critical)))
    raw = production,cargo,armament,defense
    return dict(raw=dict(zip(DIMENSIONS,raw)), weights=dict(zip(DIMENSIONS,shell['weights'])),
                burstEnergyWd=burst_energy(active_weapons+[n for n in names if n in DEFENSE],prop),
                criticalShieldCoverage=len(covered)/max(1,len(critical)),
                score=sum(w*reward(v,r) for w,v,r in zip(shell['weights'],raw,REFERENCE)))
