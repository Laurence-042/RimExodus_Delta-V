use std::collections::BinaryHeap;

use crate::bvh::{Bvh, Candidate, Kind};
use crate::grid::*;

#[derive(Clone, Copy, serde::Deserialize)]
pub struct Trace {
    pub a: Motion,
    pub b: Motion,
}
impl Trace {
    pub fn rotating(self) -> bool {
        self.a.turn != 0.0 || self.b.turn != 0.0
    }
    pub fn swapped(self) -> Self {
        Self {
            a: self.b,
            b: self.a,
        }
    }
}

fn projected(rect: Rect, q: Trace, lo: f64, hi: f64) -> Rect {
    let corners = rect.corners();
    let point = |p, t| q.b.local(q.a.world(p, t), t);
    let b = corners
        .into_iter()
        .map(|p| Rect::from_points(point(p, lo), point(p, hi)))
        .reduce(Rect::union)
        .unwrap();
    if !q.rotating() {
        return b.inflate(1e-11);
    }
    let r = q.a.position - q.b.position;
    let d = q.a.delta - q.b.delta;
    let w = q.b.turn.abs();
    let bound = w * w * (r + d * lo).length().max((r + d * hi).length())
        + 2.0 * w * d.length()
        + (q.a.turn - q.b.turn).powi(2) * rect.radius();
    b.inflate(bound * (hi - lo).powi(2) / 8.0 + 1e-11)
}

fn axes(a: Vec2, b: Vec2) -> [Vec2; 4] {
    [a, a.perpendicular(), b, b.perpendicular()]
}

fn projection(half: Vec2, basis: Vec2, axis: Vec2) -> f64 {
    half.0 * axis.dot(basis).abs() + half.1 * axis.dot(basis.perpendicular()).abs()
}

fn sweep(q: Trace, a: Rect, b: Rect, limit: f64) -> Option<f64> {
    let (u, v) = (q.a.axis(0.0), q.b.axis(0.0));
    let delta = q.b.delta - q.a.delta;
    let distance = q.b.world(b.center(), 0.0) - q.a.world(a.center(), 0.0);
    let (mut lo, mut hi) = (0.0_f64, limit);
    for axis in axes(u, v) {
        let r = projection(a.half(), u, axis) + projection(b.half(), v, axis);
        let x = distance.dot(axis);
        let v = delta.dot(axis);
        if v.abs() <= 1e-14 {
            if x.abs() > r + 1e-12 {
                return None;
            }
            continue;
        }
        let (enter, leave) = ((-r - x) / v, (r - x) / v);
        lo = lo.max(enter.min(leave));
        hi = hi.min(enter.max(leave));
        if lo > hi + 1e-12 {
            return None;
        }
    }
    Some(lo.min(limit))
}

fn gap(q: Trace, a: Rect, b: Rect, t: f64) -> f64 {
    let (u, v) = (q.a.axis(t), q.b.axis(t));
    let d = q.b.world(b.center(), t) - q.a.world(a.center(), t);
    axes(u, v)
        .into_iter()
        .map(|axis| {
            d.dot(axis).abs() - projection(a.half(), u, axis) - projection(b.half(), v, axis)
        })
        .fold(f64::NEG_INFINITY, f64::max)
}

fn speed(q: Trace, a: Rect, b: Rect) -> f64 {
    (q.a.delta - q.b.delta).length() + q.a.turn.abs() * a.radius() + q.b.turn.abs() * b.radius()
}

fn can_touch(q: Trace, a: Rect, b: Rect, lo: f64, hi: f64) -> bool {
    let mid = (lo + hi) * 0.5;
    let corners = |m: Motion, rect: Rect| {
        let corners = rect.corners();
        [lo, hi].map(|t| corners.map(|p| m.world(p, t)))
    };
    let av = corners(q.a, a);
    let bv = corners(q.b, b);
    let allowance =
        (q.a.turn.powi(2) * a.radius() + q.b.turn.powi(2) * b.radius()) * (hi - lo).powi(2) / 8.0;
    axes(q.a.axis(mid), q.b.axis(mid)).into_iter().all(|axis| {
        let range = |endpoints: [[Vec2; 4]; 2]| {
            endpoints.map(|vertices| {
                vertices
                    .into_iter()
                    .map(|p| p.dot(axis))
                    .fold((f64::INFINITY, f64::NEG_INFINITY), |(min, max), x| {
                        (min.min(x), max.max(x))
                    })
            })
        };
        let [a0, a1] = range(av);
        let [b0, b1] = range(bv);
        (b0.0 - a0.1).min(b1.0 - a1.1) <= allowance + 1e-12
            && (a0.0 - b0.1).min(a1.0 - b1.1) <= allowance + 1e-12
    })
}

fn lower(q: Trace, a: Rect, b: Rect, limit: f64) -> Option<f64> {
    if !q.rotating() {
        return sweep(q, a, b, limit);
    }
    let mid = limit * 0.5;
    (gap(q, a, b, mid) <= speed(q, a, b) * mid + 1e-11).then_some(0.0)
}

fn leaf<const C: bool>(q: Trace, a: Rect, b: Rect, limit: f64, counts: &mut Counts) -> Probe {
    if !q.rotating() {
        return sweep(q, a, b, limit).map_or(Probe::Miss, |t| Probe::Hit(Span::at(t)));
    }
    let speed = speed(q, a, b);
    first_witness(
        limit,
        ROT_EPS,
        |l, h| {
            if C {
                counts.interval_tests += 1;
            }
            gap(q, a, b, (l + h) * 0.5) <= speed * (h - l) * 0.5 + 1e-12
                && (h - l > 16.0 * ROT_EPS || can_touch(q, a, b, l, h))
        },
        |t| gap(q, a, b, t) <= 1e-12,
    )
}

fn leaf_blocks<const C: bool>(
    q: Trace,
    a: &Grid,
    b: &Grid,
    ia: usize,
    ib: usize,
    result: &mut Contacts,
    counts: &mut Counts,
) {
    if C {
        counts.blocks_visited += 1;
    }
    let range = b.rect(ib);
    for ca in a.cells(ia) {
        let rect = Rect::cell(ca);
        let p = projected(rect, q, 0.0, result.limit());
        let x0 = (p.min.0.ceil() as i32 - 1).max(range.min.0 as i32);
        let y0 = (p.min.1.ceil() as i32 - 1).max(range.min.1 as i32);
        let x1 = (p.max.0.floor() as i32).min(range.max.0 as i32 - 1);
        let y1 = (p.max.1.floor() as i32).min(range.max.1 as i32 - 1);
        for y in y0..=y1 {
            for x in x0..=x1 {
                if C {
                    counts.candidates += 1;
                }
                let cb = Cell(x, y);
                if b.filled(cb) {
                    if C {
                        counts.exact_tests += 1;
                    }
                    result.record(
                        leaf::<C>(q, rect, Rect::cell(cb), result.limit(), counts),
                        (ca, Some(cb)),
                    );
                }
            }
        }
    }
}

pub fn query<const C: bool>(
    q: Trace,
    a: &Grid,
    b: &Grid,
    trees: Option<(&Bvh, &Bvh)>,
    counts: &mut Counts,
) -> Answer {
    let mut result = Contacts::new(if q.rotating() {
        ROT_EPS * 5.0
    } else {
        TIME_EPS
    });
    let mut queue = BinaryHeap::new();
    if let Some((ta, tb)) = trees {
        let enqueue =
            |i: usize, j: usize, limit, queue: &mut BinaryHeap<_>, counts: &mut Counts| {
                if C {
                    counts.nodes_visited += 1;
                }
                if let (Some(ra), Some(rb)) = (ta.nodes[i].bounds, tb.nodes[j].bounds) {
                    if let Some(t) = lower(q, ra, rb, limit) {
                        queue.push(Candidate(t, i, j));
                    }
                }
            };
        if let (Some(i), Some(j)) = (ta.root, tb.root) {
            enqueue(i, j, 1.0, &mut queue, counts);
        }
        while let Some(Candidate(t, i, j)) = queue.pop() {
            if t > result.limit() {
                break;
            }
            match (ta.nodes[i].kind, tb.nodes[j].kind) {
                (Kind::Block(ia), Kind::Block(ib)) => {
                    leaf_blocks::<C>(q, a, b, ia, ib, &mut result, counts)
                }
                (Kind::Branch(l, r), Kind::Block(_)) => {
                    enqueue(l, j, result.limit(), &mut queue, counts);
                    enqueue(r, j, result.limit(), &mut queue, counts);
                }
                (Kind::Block(_), Kind::Branch(l, r)) => {
                    enqueue(i, l, result.limit(), &mut queue, counts);
                    enqueue(i, r, result.limit(), &mut queue, counts);
                }
                (Kind::Branch(al, ar), Kind::Branch(bl, br)) => {
                    if ta.nodes[i].bounds.unwrap().half().length()
                        >= tb.nodes[j].bounds.unwrap().half().length()
                    {
                        enqueue(al, j, result.limit(), &mut queue, counts);
                        enqueue(ar, j, result.limit(), &mut queue, counts);
                    } else {
                        enqueue(i, bl, result.limit(), &mut queue, counts);
                        enqueue(i, br, result.limit(), &mut queue, counts);
                    }
                }
            }
        }
    } else {
        for i in a.active_blocks() {
            let ra = a.rect(i);
            for j in b
                .blocks_in(projected(ra, q, 0.0, 1.0))
                .filter(|&j| b.active(j))
            {
                if C {
                    counts.nodes_visited += 1;
                }
                if let Some(t) = lower(q, ra, b.rect(j), 1.0) {
                    queue.push(Candidate(t, i, j));
                }
            }
        }
        while let Some(Candidate(t, i, j)) = queue.pop() {
            if t > result.limit() {
                break;
            }
            leaf_blocks::<C>(q, a, b, i, j, &mut result, counts);
        }
    }
    result.finish()
}
