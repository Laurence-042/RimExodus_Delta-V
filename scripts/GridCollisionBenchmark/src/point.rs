use std::collections::BinaryHeap;

use crate::bvh::{Bvh, Candidate, Kind};
use crate::grid::*;

#[derive(Clone, Copy, serde::Deserialize)]
pub struct Trace {
    pub start: Vec2,
    pub delta: Vec2,
    pub target: Motion,
}
impl Trace {
    pub fn relative(self) -> (Vec2, Vec2) {
        (
            self.target.local(self.start, 0.0),
            (self.delta - self.target.delta).inverse(self.target.axis(0.0)),
        )
    }
    pub fn local(self, t: f64) -> Vec2 {
        self.target.local(self.start + self.delta * t, t)
    }
    fn bound(self, a: f64, b: f64) -> Rect {
        let delta = self.delta - self.target.delta;
        let start = self.start - self.target.position;
        let r = (start + delta * a)
            .length()
            .max((start + delta * b).length());
        let w = self.target.turn.abs();
        let padding = (w * w * r + 2.0 * w * delta.length()) * (b - a).powi(2) / 8.0;
        Rect::from_points(self.local(a), self.local(b)).inflate(padding + 1e-12)
    }
}

pub fn entering(p: Vec2, d: Vec2, side: f64) -> Cell {
    fn along(x: f64, d: f64) -> i32 {
        let nearest = x.round();
        if (x - nearest).abs() <= 1e-10 {
            nearest as i32 - i32::from(d < 0.0)
        } else {
            x.floor() as i32
        }
    }
    Cell(along(p.0 / side, d.0), along(p.1 / side, d.1))
}

// A corner crosses both axes in a single step. Stationary axes use half-open cells.
fn walk<T>(
    p: Vec2,
    d: Vec2,
    side: f64,
    mut lo: f64,
    hi: f64,
    mut visit: impl FnMut(Cell, f64, f64) -> Option<T>,
) -> Option<T> {
    let mut cell = entering(p + d * lo, d, side);
    let advance = |origin: f64, delta: f64, i: i32| {
        if delta == 0.0 {
            return f64::INFINITY;
        }
        ((i + i32::from(delta > 0.0)) as f64 * side - origin) / delta
    };
    loop {
        let x = advance(p.0, d.0, cell.0);
        let y = advance(p.1, d.1, cell.1);
        let next = x.min(y);
        if let Some(hit) = visit(cell, lo, hi.min(next)) {
            return Some(hit);
        }
        if next > hi + 1e-12 {
            return None;
        }
        if x <= next + 1e-12 {
            cell.0 += if d.0 > 0.0 { 1 } else { -1 };
        }
        if y <= next + 1e-12 {
            cell.1 += if d.1 > 0.0 { 1 } else { -1 };
        }
        lo = next;
    }
}

fn single(hit: Option<(Cell, f64)>) -> Answer {
    hit.map_or(Answer::Miss, |(cell, t)| {
        Answer::Hit(Span::at(t.clamp(0.0, 1.0)), vec![(cell, None)])
    })
}

fn domain(size: Cell) -> Rect {
    Rect {
        min: Vec2::default(),
        max: Vec2(size.0 as f64, size.1 as f64),
    }
}

fn occupied_in(size: Cell, bounds: Rect) -> impl Iterator<Item = Cell> {
    let x0 = ((bounds.min.0.ceil() as i32) - 1).max(0);
    let y0 = ((bounds.min.1.ceil() as i32) - 1).max(0);
    let x1 = (bounds.max.0.floor() as i32).min(size.0 - 1);
    let y1 = (bounds.max.1.floor() as i32).min(size.1 - 1);
    (y0..=y1).flat_map(move |y| (x0..=x1).map(move |x| Cell(x, y)))
}

fn curved<const C: bool>(q: Trace, cell: Cell, result: &mut Contacts, counts: &mut Counts) {
    let rect = Rect::cell(cell);
    if C {
        counts.exact_tests += 1;
    }
    let t = first_witness(
        result.limit(),
        ROT_EPS,
        |lo, hi| {
            if C {
                counts.interval_tests += 1;
            }
            q.bound(lo, hi).overlaps(rect)
        },
        |t| rect.has_point(q.local(t)),
    );
    result.record(t, (cell, None));
}

pub fn flat<const C: bool>(q: Trace, size: Cell, grid: &Flat, counts: &mut Counts) -> Answer {
    if q.target.turn != 0.0 {
        let mut result = Contacts::new(ROT_EPS * 5.0);
        for cell in occupied_in(size, q.bound(0.0, 1.0)) {
            if C {
                counts.candidates += 1;
            }
            if grid.filled(cell) {
                curved::<C>(q, cell, &mut result, counts);
            }
        }
        return result.finish();
    }
    let (p, d) = q.relative();
    single(domain(size).line(p, d, 0.0, 1.0).and_then(|(lo, hi)| {
        walk(p, d, 1.0, lo, hi, |cell, t, _| {
            if C {
                counts.candidates += 1;
                counts.exact_tests += 1;
            }
            grid.filled(cell).then_some((cell, t))
        })
    }))
}

pub fn indexed<const C: bool>(
    q: Trace,
    grid: &Grid,
    tree: Option<&Bvh>,
    counts: &mut Counts,
) -> Answer {
    let (p, d) = q.relative();
    let rotating = q.target.turn != 0.0;
    let mut contacts = Contacts::new(if rotating { ROT_EPS * 5.0 } else { TIME_EPS });
    let mut hit: Option<(Cell, f64)> = None;
    let bounds = rotating.then(|| q.bound(0.0, 1.0));
    let lower = |rect: Rect| {
        if let Some(bounds) = bounds {
            return bounds.overlaps(rect).then_some((0.0, 1.0));
        }
        rect.line(p, d, 0.0, 1.0)
    };
    let mut visit = |block, lo, hi, counts: &mut Counts| {
        if !grid.active(block) {
            return None;
        }
        if rotating {
            let area = bounds.unwrap();
            for cell in grid.cells(block) {
                if C {
                    counts.candidates += 1;
                }
                if Rect::cell(cell).overlaps(area) {
                    curved::<C>(q, cell, &mut contacts, counts);
                }
            }
            None
        } else {
            walk(p, d, 1.0, lo, hi, |cell, t, _| {
                let owner = Cell(cell.0.div_euclid(grid.side), cell.1.div_euclid(grid.side));
                if grid.block(owner) != Some(block) {
                    return None;
                }
                if C {
                    counts.candidates += 1;
                    counts.exact_tests += 1;
                }
                grid.filled(cell).then_some((cell, t))
            })
        }
    };
    if let Some(tree) = tree {
        let mut queue = BinaryHeap::new();
        let enqueue = |node: usize, queue: &mut BinaryHeap<_>, counts: &mut Counts| {
            if C {
                counts.nodes_visited += 1;
            }
            if let Some((lo, _)) = tree.nodes[node].bounds.and_then(lower) {
                queue.push(Candidate(lo, node, 0));
            }
        };
        if let Some(root) = tree.root {
            enqueue(root, &mut queue, counts);
        }
        while let Some(Candidate(lo, node, _)) = queue.pop() {
            if hit.is_some_and(|(_, t)| lo > t + TIME_EPS) {
                break;
            }
            match tree.nodes[node].kind {
                Kind::Block(id) => {
                    if C {
                        counts.blocks_visited += 1;
                    }
                    let (_, hi) = lower(grid.rect(id)).unwrap();
                    if let Some(next) = visit(id, lo, hi, counts) {
                        if hit.is_none_or(|(_, t)| next.1 < t) {
                            hit = Some(next);
                        }
                    }
                }
                Kind::Branch(a, b) => {
                    enqueue(a, &mut queue, counts);
                    enqueue(b, &mut queue, counts);
                }
            }
        }
    } else if let Some(area) = bounds {
        for id in grid.blocks_in(area) {
            if C {
                counts.blocks_visited += 1;
            }
            visit(id, 0.0, 1.0, counts);
        }
    } else if let Some((lo, hi)) = domain(grid.size).line(p, d, 0.0, 1.0) {
        hit = walk(p, d, grid.side as f64, lo, hi, |cell, a, b| {
            if C {
                counts.blocks_visited += 1;
            }
            grid.block(cell).and_then(|id| visit(id, a, b, counts))
        });
    }
    drop(visit);
    if rotating {
        contacts.finish()
    } else {
        single(hit)
    }
}
