use crate::{grid::*, grid_pair, point};

fn cross(a: Vec2, b: Vec2) -> f64 {
    a.0 * b.1 - a.1 * b.0
}
fn world(m: Motion, c: Cell, t: f64) -> [Vec2; 4] {
    Rect::cell(c).corners().map(|p| m.world(p, t))
}
fn edges(poly: &[Vec2]) -> impl Iterator<Item = (Vec2, Vec2)> + '_ {
    poly.iter()
        .copied()
        .zip(poly.iter().copied().cycle().skip(1))
}
fn in_polygon(p: Vec2, poly: &[Vec2]) -> bool {
    edges(poly).all(|(a, b)| cross(b - a, p - a) >= -1e-12)
}
fn distance(p: Vec2, poly: &[Vec2]) -> f64 {
    if in_polygon(p, poly) {
        return 0.0;
    }
    edges(poly)
        .map(|(a, b)| {
            let v = b - a;
            let factor = ((p - a).dot(v) / v.dot(v)).clamp(0.0, 1.0);
            (p - a - v * factor).length()
        })
        .fold(f64::INFINITY, f64::min)
}
fn hull(mut points: Vec<Vec2>) -> Vec<Vec2> {
    points.sort_unstable_by(|a, b| a.partial_cmp(b).unwrap());
    points.dedup();
    let mut ring = Vec::new();
    for list in [points.clone(), points.into_iter().rev().collect()] {
        let offset = ring.len();
        for p in list {
            while ring.len() >= offset + 2 {
                let a = ring[ring.len() - 2];
                let b = ring[ring.len() - 1];
                if cross(b - a, p - b) > 1e-12 {
                    break;
                }
                ring.pop();
            }
            ring.push(p);
        }
        ring.pop();
    }
    ring
}
fn convex_sweep(domain: &[Vec2], d: Vec2) -> Probe {
    let (mut lo, mut hi) = (0.0_f64, 1.0_f64);
    for (p, q) in edges(domain) {
        let v = cross(q - p, d);
        let c = cross(q - p, p);
        if v > 1e-14 {
            lo = lo.max(c / v);
        } else if v < -1e-14 {
            hi = hi.min(c / v);
        } else if c > 1e-12 {
            return Probe::Miss;
        }
        if lo > hi + 1e-12 {
            return Probe::Miss;
        }
    }
    Probe::Hit(Span::at(lo.clamp(0.0, 1.0)))
}
fn polygon_distance(a: &[Vec2], b: &[Vec2]) -> f64 {
    for (p, q) in edges(a) {
        for (r, s) in edges(b) {
            let d = cross(q - p, s - r);
            if d.abs() < 1e-14 {
                continue;
            }
            let u = cross(r - p, s - r) / d;
            let v = cross(r - p, q - p) / d;
            if (0.0..=1.0).contains(&u) && (0.0..=1.0).contains(&v) {
                return 0.0;
            }
        }
    }
    a.iter()
        .map(|&v| distance(v, b))
        .chain(b.iter().map(|&v| distance(v, a)))
        .fold(f64::INFINITY, f64::min)
}

fn rotating(speed: f64, d: impl Fn(f64) -> f64) -> Probe {
    first_witness(
        1.0,
        ROT_EPS / 4.0,
        |l, h| d((l + h) * 0.5) <= speed * (h - l) * 0.5 + 1e-12,
        |t| d(t) <= 1e-12,
    )
}

pub fn point(q: point::Trace, cells: &[Cell]) -> Answer {
    let mut result = Contacts::new(if q.target.turn == 0.0 {
        TIME_EPS
    } else {
        ROT_EPS * 5.0
    });
    for &c in cells {
        let rect = Rect::cell(c);
        let probe = if q.target.turn == 0.0 {
            let (p, d) = q.relative();
            match rect.line(p, d, 0.0, 1.0) {
                Some((t, end)) => {
                    let at = p + d * t;
                    let valid_axis = |at: f64, d: f64, min: f64, max: f64| {
                        (at > min + 1e-10 || d >= 0.0) && (at < max - 1e-10 || d < 0.0)
                    };
                    if end >= t
                        && valid_axis(at.0, d.0, rect.min.0, rect.max.0)
                        && valid_axis(at.1, d.1, rect.min.1, rect.max.1)
                    {
                        Probe::Hit(Span::at(t))
                    } else {
                        Probe::Miss
                    }
                }
                None => Probe::Miss,
            }
        } else {
            let speed = (q.delta - q.target.delta).length() + q.target.turn.abs() * rect.radius();
            rotating(speed, |t| {
                distance(q.start + q.delta * t, &world(q.target, c, t))
            })
        };
        result.record(probe, (c, None));
    }
    result.finish()
}

pub fn pair(q: grid_pair::Trace, a: &[Cell], b: &[Cell]) -> Answer {
    let mut result = Contacts::new(if q.rotating() {
        ROT_EPS * 5.0
    } else {
        TIME_EPS
    });
    for &ca in a {
        for &cb in b {
            let probe = if q.rotating() {
                let speed = (q.a.delta - q.b.delta).length()
                    + q.a.turn.abs() * Rect::cell(ca).radius()
                    + q.b.turn.abs() * Rect::cell(cb).radius();
                rotating(speed, |t| {
                    polygon_distance(&world(q.a, ca, t), &world(q.b, cb, t))
                })
            } else {
                let av = world(q.a, ca, 0.0);
                let bv = world(q.b, cb, 0.0);
                let domain = hull(av.into_iter().flat_map(|p| bv.map(|r| r - p)).collect());
                convex_sweep(&domain, q.a.delta - q.b.delta)
            };
            result.record(probe, (ca, Some(cb)));
        }
    }
    result.finish()
}
