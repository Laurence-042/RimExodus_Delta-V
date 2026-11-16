use std::ops::{Add, Mul, Sub};

#[derive(Clone, Copy, Debug, Default, PartialEq, PartialOrd, serde::Deserialize)]
pub struct Vec2(pub f64, pub f64);
impl Vec2 {
    pub fn dot(self, rhs: Self) -> f64 {
        self.0 * rhs.0 + self.1 * rhs.1
    }
    pub fn length(self) -> f64 {
        self.0.hypot(self.1)
    }
    pub fn perpendicular(self) -> Self {
        Self(-self.1, self.0)
    }
    pub fn rotate(self, axis: Self) -> Self {
        axis * self.0 + axis.perpendicular() * self.1
    }
    pub fn inverse(self, axis: Self) -> Self {
        Self(self.dot(axis), self.dot(axis.perpendicular()))
    }
}
impl Add for Vec2 {
    type Output = Self;
    fn add(self, other: Self) -> Self {
        Self(self.0 + other.0, self.1 + other.1)
    }
}
impl Sub for Vec2 {
    type Output = Self;
    fn sub(self, other: Self) -> Self {
        Self(self.0 - other.0, self.1 - other.1)
    }
}
impl Mul<f64> for Vec2 {
    type Output = Self;
    fn mul(self, scale: f64) -> Self {
        Self(self.0 * scale, self.1 * scale)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Ord, PartialOrd, serde::Deserialize)]
pub struct Cell(pub i32, pub i32);

#[derive(Clone, Copy, Debug, Default, serde::Deserialize)]
#[serde(default)]
pub struct Motion {
    pub position: Vec2,
    pub delta: Vec2,
    pub angle: f64,
    pub turn: f64,
}
impl Motion {
    pub fn axis(self, t: f64) -> Vec2 {
        let (sin, cos) = (self.angle + self.turn * t).sin_cos();
        Vec2(cos, sin)
    }
    pub fn world(self, point: Vec2, t: f64) -> Vec2 {
        self.position + self.delta * t + point.rotate(self.axis(t))
    }
    pub fn local(self, point: Vec2, t: f64) -> Vec2 {
        (point - self.position - self.delta * t).inverse(self.axis(t))
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Rect {
    pub min: Vec2,
    pub max: Vec2,
}
impl Rect {
    pub fn cell(cell: Cell) -> Self {
        Self::square(cell, 1)
    }
    pub fn square(cell: Cell, side: i32) -> Self {
        Self {
            min: Vec2(cell.0 as f64, cell.1 as f64),
            max: Vec2((cell.0 + side) as f64, (cell.1 + side) as f64),
        }
    }
    pub fn from_points(a: Vec2, b: Vec2) -> Self {
        Self {
            min: Vec2(a.0.min(b.0), a.1.min(b.1)),
            max: Vec2(a.0.max(b.0), a.1.max(b.1)),
        }
    }
    pub fn union(self, other: Self) -> Self {
        Self::from_points(
            Vec2(self.min.0.min(other.min.0), self.min.1.min(other.min.1)),
            Vec2(self.max.0.max(other.max.0), self.max.1.max(other.max.1)),
        )
    }
    pub fn inflate(self, padding: f64) -> Self {
        let d = Vec2(padding, padding);
        Self {
            min: self.min - d,
            max: self.max + d,
        }
    }
    pub fn overlaps(self, other: Self) -> bool {
        self.min.0 <= other.max.0
            && self.max.0 >= other.min.0
            && self.min.1 <= other.max.1
            && self.max.1 >= other.min.1
    }
    pub fn has_point(self, p: Vec2) -> bool {
        self.overlaps(Self::from_points(p, p))
    }
    pub fn center(self) -> Vec2 {
        (self.min + self.max) * 0.5
    }
    pub fn half(self) -> Vec2 {
        (self.max - self.min) * 0.5
    }
    pub fn corners(self) -> [Vec2; 4] {
        [
            self.min,
            Vec2(self.max.0, self.min.1),
            self.max,
            Vec2(self.min.0, self.max.1),
        ]
    }
    pub fn radius(self) -> f64 {
        Vec2(
            self.min.0.abs().max(self.max.0.abs()),
            self.min.1.abs().max(self.max.1.abs()),
        )
        .length()
    }
    pub fn line(self, p: Vec2, d: Vec2, mut lo: f64, mut hi: f64) -> Option<(f64, f64)> {
        for (start, step, min, max) in [
            (p.0, d.0, self.min.0, self.max.0),
            (p.1, d.1, self.min.1, self.max.1),
        ] {
            if step == 0.0 {
                if start < min || start > max {
                    return None;
                }
            } else {
                let a = (min - start) / step;
                let b = (max - start) / step;
                lo = lo.max(a.min(b));
                hi = hi.min(a.max(b));
                if lo > hi {
                    return None;
                }
            }
        }
        Some((lo, hi))
    }
}

#[derive(Clone, Copy, Debug)]
pub struct Span {
    pub lo: f64,
    pub hi: f64,
}
impl Span {
    pub fn at(t: f64) -> Self {
        Self { lo: t, hi: t }
    }
}
pub const TIME_EPS: f64 = 1.0e-9;
pub const ROT_EPS: f64 = 2.0e-7;

#[derive(Clone, Copy, Debug)]
pub enum Probe {
    Miss,
    Hit(Span),
    Unresolved(f64),
}

pub type CellPair = (Cell, Option<Cell>);

#[derive(Debug)]
pub enum Answer {
    Miss,
    Hit(Span, Vec<CellPair>),
    Unresolved(f64),
}

pub struct Contacts {
    time: Option<Span>,
    cells: Vec<(Span, CellPair)>,
    uncertain: f64,
    tolerance: f64,
}
impl Contacts {
    pub fn new(tolerance: f64) -> Self {
        Self {
            time: None,
            cells: Vec::new(),
            uncertain: f64::INFINITY,
            tolerance,
        }
    }
    pub fn limit(&self) -> f64 {
        self.time
            .map_or(1.0, |time| (time.hi + self.tolerance).min(1.0))
    }
    pub fn record(&mut self, probe: Probe, pair: CellPair) {
        match probe {
            Probe::Miss => {}
            Probe::Unresolved(t) => self.uncertain = self.uncertain.min(t),
            Probe::Hit(next) => {
                match &mut self.time {
                    Some(time) if next.lo > time.hi + self.tolerance => return,
                    Some(time) if time.lo <= next.hi + self.tolerance => {
                        time.lo = time.lo.min(next.lo);
                        time.hi = time.hi.min(next.hi);
                    }
                    _ => {
                        self.time = Some(next);
                        self.cells.clear();
                    }
                }
                self.cells.push((next, pair));
            }
        }
    }
    pub fn finish(mut self) -> Answer {
        if let Some(mut time) = self.time {
            if self.uncertain + self.tolerance < time.hi {
                return Answer::Unresolved(self.uncertain);
            }
            time.lo = time.lo.min(self.uncertain);
            self.cells
                .retain(|(span, _)| span.lo <= time.hi + self.tolerance);
            let mut cells: Vec<_> = self.cells.into_iter().map(|(_, pair)| pair).collect();
            cells.sort_unstable();
            cells.dedup();
            Answer::Hit(time, cells)
        } else if self.uncertain.is_finite() {
            Answer::Unresolved(self.uncertain)
        } else {
            Answer::Miss
        }
    }
}

// Left-to-right interval search. An ambiguous interval is only transferable to
// a witnessed hit when both fit inside the advertised time tolerance.
pub fn first_witness(
    limit: f64,
    resolution: f64,
    mut possible: impl FnMut(f64, f64) -> bool,
    contains: impl Fn(f64) -> bool,
) -> Probe {
    let mut stack = Vec::with_capacity(32);
    stack.push((0.0, limit.min(1.0)));
    let mut earliest = None;
    let mut steps = 0;
    while let Some((a, b)) = stack.pop() {
        steps += 1;
        if steps > 32_768 || earliest.is_some_and(|x| a > x + 4.0 * resolution) {
            return Probe::Unresolved(earliest.unwrap_or(a));
        }
        if !possible(a, b) {
            continue;
        }
        if contains(a) {
            return Probe::Hit(Span {
                lo: earliest.unwrap_or(a),
                hi: a,
            });
        }
        if b - a <= resolution {
            let mid = (a + b) * 0.5;
            if contains(mid) || contains(b) {
                return Probe::Hit(Span {
                    lo: earliest.unwrap_or(a),
                    hi: if contains(mid) { mid } else { b },
                });
            }
            earliest.get_or_insert(a);
        } else {
            let mid = (a + b) * 0.5;
            stack.push((mid, b));
            stack.push((a, mid));
        }
    }
    earliest.map_or(Probe::Miss, Probe::Unresolved)
}

#[derive(Default, Debug, serde::Serialize)]
pub struct Counts {
    pub nodes_visited: u64,
    pub blocks_visited: u64,
    pub candidates: u64,
    pub exact_tests: u64,
    pub interval_tests: u64,
    pub cells_deleted: u64,
    pub blocks_emptied: u64,
    pub index_nodes_updated: u64,
}

#[derive(Clone)]
struct Mask {
    words: [u64; 4],
    count: usize,
}
impl Mask {
    fn empty() -> Self {
        Self {
            words: [0; 4],
            count: 0,
        }
    }
    fn set(&mut self, bit: usize, filled: bool) -> bool {
        let flag = 1 << (bit % 64);
        let word = &mut self.words[bit / 64];
        if (*word & flag != 0) == filled {
            return false;
        }
        *word ^= flag;
        if filled {
            self.count += 1;
        } else {
            self.count -= 1;
        }
        true
    }
    fn test(&self, bit: usize) -> bool {
        self.words[bit / 64] & (1 << (bit % 64)) != 0
    }
}

#[derive(Clone)]
pub struct Grid {
    pub size: Cell,
    pub side: i32,
    pub block_width: i32,
    block_height: i32,
    masks: Vec<Mask>,
}
impl Grid {
    pub fn new(size: Cell, side: i32, cells: &[Cell]) -> Self {
        assert!(matches!(side, 8 | 16));
        let w = (size.0 + side - 1) / side;
        let h = (size.1 + side - 1) / side;
        let mut grid = Self {
            size,
            side,
            block_width: w,
            block_height: h,
            masks: vec![Mask::empty(); (w * h) as usize],
        };
        for &cell in cells {
            let (block, bit) = grid.location(cell).expect("fixture cell outside grid");
            grid.masks[block].set(bit, true);
        }
        grid
    }
    fn location(&self, cell: Cell) -> Option<(usize, usize)> {
        if cell.0 < 0 || cell.1 < 0 || cell.0 >= self.size.0 || cell.1 >= self.size.1 {
            return None;
        }
        Some((
            (cell.1 / self.side * self.block_width + cell.0 / self.side) as usize,
            ((cell.1 % self.side) * self.side + cell.0 % self.side) as usize,
        ))
    }
    pub fn filled(&self, cell: Cell) -> bool {
        self.location(cell)
            .is_some_and(|(block, bit)| self.masks[block].test(bit))
    }
    pub fn delete<const C: bool>(&mut self, cell: Cell, counts: &mut Counts) -> Option<usize> {
        let (block, bit) = self.location(cell)?;
        if !self.masks[block].set(bit, false) {
            return None;
        }
        if C {
            counts.cells_deleted += 1;
        }
        if self.masks[block].count == 0 {
            if C {
                counts.blocks_emptied += 1;
            }
            Some(block)
        } else {
            None
        }
    }
    pub fn active(&self, id: usize) -> bool {
        self.masks[id].count != 0
    }
    pub fn active_blocks(&self) -> impl Iterator<Item = usize> + '_ {
        (0..self.masks.len()).filter(|&i| self.active(i))
    }
    pub fn capacity(&self) -> usize {
        self.masks.len()
    }
    pub fn block_coord(&self, id: usize) -> Cell {
        Cell(id as i32 % self.block_width, id as i32 / self.block_width)
    }
    pub fn block(&self, cell: Cell) -> Option<usize> {
        if cell.0 < 0 || cell.1 < 0 || cell.0 >= self.block_width || cell.1 >= self.block_height {
            return None;
        }
        Some((cell.1 * self.block_width + cell.0) as usize)
    }
    pub fn rect(&self, id: usize) -> Rect {
        let c = self.block_coord(id);
        let origin = Cell(c.0 * self.side, c.1 * self.side);
        Rect {
            min: Vec2(origin.0 as f64, origin.1 as f64),
            max: Vec2(
                (origin.0 + self.side).min(self.size.0) as f64,
                (origin.1 + self.side).min(self.size.1) as f64,
            ),
        }
    }
    pub fn cells(&self, id: usize) -> impl Iterator<Item = Cell> + '_ {
        let origin = self.block_coord(id);
        let side = self.side;
        self.masks[id]
            .words
            .into_iter()
            .enumerate()
            .flat_map(move |(word, mut bits)| {
                std::iter::from_fn(move || {
                    if bits == 0 {
                        return None;
                    }
                    let bit = word as i32 * 64 + bits.trailing_zeros() as i32;
                    bits &= bits - 1;
                    Some(Cell(
                        origin.0 * side + bit % side,
                        origin.1 * side + bit / side,
                    ))
                })
            })
    }
    pub fn blocks_in(&self, area: Rect) -> impl Iterator<Item = usize> + '_ {
        let scale = self.side as f64;
        let x0 = ((area.min.0 / scale).ceil() as i32 - 1).max(0);
        let y0 = ((area.min.1 / scale).ceil() as i32 - 1).max(0);
        let x1 = ((area.max.0 / scale).floor() as i32).min(self.block_width - 1);
        let y1 = ((area.max.1 / scale).floor() as i32).min(self.block_height - 1);
        (y0..=y1).flat_map(move |y| (x0..=x1).map(move |x| (y * self.block_width + x) as usize))
    }
}

pub struct Flat {
    size: Cell,
    words: Vec<u64>,
}
impl Flat {
    pub fn new(size: Cell, cells: &[Cell]) -> Self {
        let mut grid = Self {
            size,
            words: vec![0; (size.0 as usize * size.1 as usize).div_ceil(64)],
        };
        for &cell in cells {
            let bit = grid.bit(cell).expect("fixture cell outside grid");
            grid.words[bit / 64] |= 1 << (bit % 64);
        }
        grid
    }
    fn bit(&self, c: Cell) -> Option<usize> {
        (c.0 >= 0 && c.1 >= 0 && c.0 < self.size.0 && c.1 < self.size.1)
            .then_some((c.1 * self.size.0 + c.0) as usize)
    }
    pub fn filled(&self, cell: Cell) -> bool {
        self.bit(cell)
            .is_some_and(|i| self.words[i / 64] & (1 << (i % 64)) != 0)
    }
    pub fn delete<const C: bool>(&mut self, cell: Cell, counts: &mut Counts) {
        if let Some(i) = self.bit(cell) {
            let mask = 1 << (i % 64);
            if C && self.words[i / 64] & mask != 0 {
                counts.cells_deleted += 1;
            }
            self.words[i / 64] &= !mask;
        }
    }
}
