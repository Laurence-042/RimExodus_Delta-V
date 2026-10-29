"""Offline math experiment: cells/seconds; exact velocity lead, point shell, disk hull.
No AI/gameplay changes. Disk intersection is not a building-grid hit probability.
"""
import json, math
from pathlib import Path

def bisect(f,lo,hi):
    assert f(lo)<=0<=f(hi)
    for _ in range(80):
        m=(lo+hi)/2
        if f(m)<0: lo=m
        else: hi=m
    return (lo+hi)/2

def golden(f,lo,hi):
    r=(math.sqrt(5)-1)/2
    a,b=hi-r*(hi-lo),lo+r*(hi-lo)
    fa,fb=f(a),f(b)
    for _ in range(80):
        if fa<fb:
            hi,b,fb=b,a,fa
            a=hi-r*(hi-lo); fa=f(a)
        else:
            lo,a,fa=a,b,fb
            b=lo+r*(hi-lo); fb=f(b)
    t=(lo+hi)/2
    return t,f(t)

def transverse_min(d,c,a):
    t=bisect(lambda t:a*a*t**3+2*c*c*t-2*c*d,0,d/c)
    return math.hypot(d-c*t,a*t*t/2),t

def critical(d,c,r):
    if r>=d:return math.inf
    u=4*(r/d)**2/(math.sqrt(1+8*(r/d)**2)+1)
    return c*c/d*math.sqrt(2*u/(1-u)**3)

def delayed_drag_min(d,c,a,delay=0,half_life=0):
    lam=math.log(2)/half_life if half_life else 0
    def y(t):
        s=max(0,t-delay)
        return a*(s/lam+math.expm1(-lam*s)/lam**2) if lam else a*s*s/2
    t,d2=golden(lambda t:(d-c*t)**2+y(t)**2,0,d/c)
    return math.sqrt(d2),t

def adjusted_threshold(d,c,r,delay=0,half_life=0):
    if d<=r or delay>=(d-r)/c:return math.inf
    f=lambda a:delayed_drag_min(d,c,a,delay,half_life)[0]-r
    hi=1
    for _ in range(50):
        if f(hi)>=0:break
        hi*=2
    return bisect(f,0,hi)

def circle_min(d,c,v):
    assert 0<=v<c
    vx=math.sqrt(c*c-v*v); w=v/d
    f=lambda t:(d*math.cos(w*t)-vx*t)**2+(d*math.sin(w*t)-v*t)**2
    # After 2D/c the shell is more than D away from every point of the orbit.
    grid=[2*d/c*i/500 for i in range(501)]
    values=[f(t) for t in grid]
    candidates=[(grid[0],values[0]),(grid[-1],values[-1])]
    for i in range(1,500):
        if values[i]<=values[i-1] and values[i]<=values[i+1]:
            candidates.append(golden(f,grid[i-1],grid[i+1]))
    t,d2=min(candidates,key=lambda p:p[1])
    return math.sqrt(d2),t

def validate():
    checks=0
    for d in (30,75,100,115,150,200,400):
        for c in (30,45,90):
            for a in (0,1,3,5,10,100):
                exact,t=transverse_min(d,c,a)
                numeric,_=delayed_drag_min(d,c,a)
                assert abs(exact-numeric)<1e-7
                assert exact<=a*(d/c)**2/2+1e-7
                checks+=2
            for r in (5,10,13.9):
                a=critical(d,c,r)
                assert abs(transverse_min(d,c,a)[0]-r)<1e-7
                assert transverse_min(d,c,a*.99)[0]<r
                assert transverse_min(d,c,a*1.01)[0]>r
                checks+=3
    for v in (1,3,6,12,16,30,44):
        assert abs(circle_min(230,90,2*v)[0]-2*circle_min(115,45,v)[0])<1e-7
        checks+=1
    return checks

if __name__=='__main__':
    result={'checks':validate(),'thresholds':[],'offsets':[],'circle':[]}
    for d in (50,75,100,115,150,170,200):
        result['thresholds'].append({'distance':d,'width10':critical(d,45,5),
          'width20':critical(d,45,10),'radius13_9':critical(d,45,13.9),
          'radius13_9_drag8':adjusted_threshold(d,45,13.9,0,8),
          'radius13_9_delay0_5_drag8':adjusted_threshold(d,45,13.9,.5,8)})
        for a in (1,3,5):
            result['offsets'].append({'distance':d,'a':a,'nominal':a*(d/45)**2/2,
                                     'closest':transverse_min(d,45,a)[0]})
    for v in (6,10,12,16,20,25,30):
        result['circle'].append({'distance':115,'speed':v,'centripetal_a':v*v/115,
          'naive_half_a_t2':.5*(v*v/115)*(115/math.sqrt(45**2-v*v))**2,
          'closest':circle_min(115,45,v)[0],
          'same_magnitude_transverse':transverse_min(115,45,v*v/115)[0]})
    destination=Path(__file__).resolve().parents[2]/'Source/obj/CombatManeuverSimulation/ballistic-limits.json'
    destination.parent.mkdir(parents=True,exist_ok=True)
    destination.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result,indent=2))
