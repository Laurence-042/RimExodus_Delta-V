mod bvh;
mod grid;
mod grid_pair;
mod oracle;
mod point;

use bvh::Bvh;
use grid::*;
use serde::{Deserialize, Serialize};
use std::{hint::black_box, time::Instant};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Backend {
    Dda,
    Fixed8,
    Fixed16,
    Bvh8,
    Bvh16,
}
impl Backend {
    const ALL: [Self; 5] = [
        Self::Dda,
        Self::Fixed8,
        Self::Fixed16,
        Self::Bvh8,
        Self::Bvh16,
    ];
    fn name(self) -> &'static str {
        match self {
            Self::Dda => "dda",
            Self::Fixed8 => "fixed8",
            Self::Fixed16 => "fixed16",
            Self::Bvh8 => "bvh8",
            Self::Bvh16 => "bvh16",
        }
    }
}

struct Shape {
    grid: Grid,
    flat: Option<Flat>,
    bvh: Option<Bvh>,
}
impl Shape {
    fn new(backend: Backend, size: Cell, cells: &[Cell]) -> Self {
        let side = if matches!(backend, Backend::Fixed16 | Backend::Bvh16) {
            16
        } else {
            8
        };
        let grid = Grid::new(size, side, cells);
        Self {
            flat: (backend == Backend::Dda).then(|| Flat::new(size, cells)),
            bvh: matches!(backend, Backend::Bvh8 | Backend::Bvh16).then(|| Bvh::new(&grid)),
            grid,
        }
    }
    fn erase<const C: bool>(&mut self, c: Cell, counts: &mut Counts) {
        if let Some(flat) = &mut self.flat {
            return flat.delete::<C>(c, counts);
        }
        if let Some(id) = self.grid.delete::<C>(c, counts) {
            if let Some(tree) = &mut self.bvh {
                tree.empty_block::<C>(id, counts);
            }
        }
    }
}

#[derive(Clone, Copy)]
enum Trace {
    Point(point::Trace),
    Pair(grid_pair::Trace),
}
impl Trace {
    fn run<const C: bool>(self, a: &Shape, b: &Shape, counts: &mut Counts) -> Answer {
        match self {
            Self::Point(q) => {
                if let Some(flat) = &b.flat {
                    point::flat::<C>(q, b.grid.size, flat, counts)
                } else {
                    point::indexed::<C>(q, &b.grid, b.bvh.as_ref(), counts)
                }
            }
            Self::Pair(q) => grid_pair::query::<C>(
                q,
                &a.grid,
                &b.grid,
                a.bvh.as_ref().zip(b.bvh.as_ref()),
                counts,
            ),
        }
    }
    fn oracle(self, a: &[Cell], b: &[Cell]) -> Answer {
        match self {
            Self::Point(q) => oracle::point(q, b),
            Self::Pair(q) => oracle::pair(q, a, b),
        }
    }
    fn rotating(self) -> bool {
        match self {
            Self::Point(q) => q.target.turn != 0.0,
            Self::Pair(q) => q.rotating(),
        }
    }
}

#[derive(Deserialize)]
struct Case<T> {
    name: String,
    a: Vec<Cell>,
    #[serde(default)]
    b: Vec<Cell>,
    delete: Vec<Cell>,
    expected: [bool; 2],
    trace: T,
}
#[derive(Deserialize)]
struct Scale {
    name: String,
    dimension: i32,
    ships: usize,
    batches: usize,
}
#[derive(Deserialize)]
struct Config {
    points: Vec<Case<point::Trace>>,
    pairs: Vec<Case<grid_pair::Trace>>,
    scales: Vec<Scale>,
}

#[derive(Default, Serialize)]
struct Outcomes {
    hit: u64,
    miss: u64,
    unresolved: u64,
}
impl Outcomes {
    fn record(&mut self, answer: &Answer) {
        match answer {
            Answer::Hit(..) => self.hit += 1,
            Answer::Miss => self.miss += 1,
            Answer::Unresolved(_) => self.unresolved += 1,
        }
    }
}

fn same(a: &Answer, b: &Answer, tolerance: f64) -> bool {
    match (a, b) {
        (Answer::Miss, Answer::Miss) => true,
        (Answer::Hit(t, ca), Answer::Hit(u, cb)) => {
            ca == cb && t.lo <= u.hi + tolerance && u.lo <= t.hi + tolerance
        }
        _ => false,
    }
}
fn label(a: &Answer) -> &'static str {
    match a {
        Answer::Hit(..) => "hit",
        Answer::Miss => "miss",
        Answer::Unresolved(_) => "unresolved",
    }
}
fn verify_case<T>(kind: &str, case: &Case<T>, trace: Trace, a: &[Cell], b: &[Cell]) {
    let size = Cell(32, 32);
    let after = b
        .iter()
        .copied()
        .filter(|c| !case.delete.contains(c))
        .collect::<Vec<_>>();
    let expected = [trace.oracle(a, b), trace.oracle(a, &after)];
    for (want, got) in case.expected.into_iter().zip(&expected) {
        assert_eq!(
            label(got),
            if want { "hit" } else { "miss" },
            "fixture {}: {got:?}",
            case.name
        );
    }
    let tolerance = if trace.rotating() {
        ROT_EPS * 5.0
    } else {
        TIME_EPS
    };
    for backend in Backend::ALL {
        if kind == "pair" && backend == Backend::Dda {
            continue;
        }
        let mut counts = Counts::default();
        let a = Shape::new(backend, size, a);
        let mut b = Shape::new(backend, size, b);
        for (after, want) in expected.iter().enumerate() {
            if after == 1 {
                for &c in &case.delete {
                    b.erase::<true>(c, &mut counts);
                }
            }
            let got = trace.run::<true>(&a, &b, &mut counts);
            let status = match &got {
                Answer::Unresolved(t) => {
                    assert!((0.0..=1.0).contains(t));
                    "unresolved"
                }
                _ if same(want, &got, tolerance) => "ok",
                _ => "mismatch",
            };
            println!(
                "{}",
                serde_json::json!({
                    "kind":kind,"case":case.name,"after":after==1,"backend":backend.name(),"status":status,
                    "oracle":format!("{want:?}"),"answer":format!("{got:?}")
                })
            );
            assert_eq!(
                status,
                "ok",
                "{kind} {} {} after={after}",
                case.name,
                backend.name()
            );
        }
    }
    if let Trace::Pair(q) = trace {
        let swapped = oracle::pair(q.swapped(), b, a);
        let mut expected = expected.into_iter().next().unwrap();
        if let Answer::Hit(_, cells) = &mut expected {
            for (a, b) in cells.iter_mut() {
                (*a, *b) = (b.unwrap(), Some(*a));
            }
            cells.sort_unstable();
        }
        assert!(
            same(&expected, &swapped, tolerance),
            "reversal failed: {}",
            case.name
        );
    }
}

fn verify(config: &Config) {
    for case in &config.points {
        verify_case("point", case, Trace::Point(case.trace), &[], &case.a);
    }
    for case in &config.pairs {
        verify_case("pair", case, Trace::Pair(case.trace), &case.a, &case.b);
    }
    eprintln!("Native oracles passed.");
    for pair in [false, true] {
        for scale in &config.scales {
            for damage in DAMAGE {
                for linear in [false, true] {
                    verify_workload(scale, pair, damage, linear);
                }
            }
        }
    }
}

struct Random(u64);
impl Random {
    fn next(&mut self) -> u32 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        (self.0 >> 16) as u32
    }
}

const DAMAGE: [&str; 4] = ["single", "cluster", "scattered", "empty-block"];
struct Job {
    a: usize,
    b: usize,
    edit: Vec<Cell>,
    queries: [Trace; 8],
}

fn workload(scale: &Scale, pair: bool, damage: &str, linear: bool) -> (Vec<Vec<Cell>>, Vec<Job>) {
    let n = scale.dimension;
    let mut rng = Random(0x75a4_ab52_22ae_0c35 + n as u64);
    let ships: Vec<Vec<Cell>> = (0..scale.ships)
        .map(|ship| {
            (0..n)
                .flat_map(|y| (0..n).map(move |x| Cell(x, y)))
                .filter(|&Cell(x, y)| {
                    let frame = x < 2 || y < 2 || x >= n - 2 || y >= n - 2;
                    let notch = x >= n - 5 && y >= n / 3 && y <= n * 2 / 3;
                    let roll = rng.next() % 100;
                    !notch
                        && (frame
                            || (x + ship as i32) % 9 == 0
                            || y % 11 == 0
                            || roll < if n < 32 { 37 } else { 12 })
                })
                .collect()
        })
        .collect();
    let jobs = (0..scale.batches)
        .map(|i| {
            let a = (2 * i + 1) % scale.ships;
            let b = (a + 1) % scale.ships;
            let edit = match damage {
                "single" => vec![Cell(0, n / 2)],
                "cluster" => ships[b]
                    .iter()
                    .copied()
                    .filter(|&Cell(x, y)| x < 8 && (y - n / 2).abs() < 5)
                    .collect(),
                "empty-block" => ships[b]
                    .iter()
                    .copied()
                    .filter(|&Cell(x, y)| x < 16 && y < 16)
                    .collect(),
                "scattered" => ships[b]
                    .iter()
                    .copied()
                    .enumerate()
                    .filter_map(|(index, cell)| ((index + i) % 17 == 0).then_some(cell))
                    .take(64)
                    .collect(),
                _ => unreachable!(),
            };
            let queries = std::array::from_fn(|j| {
                let rotates = !linear && (j == 4 || j == 6);
                let y = match j {
                    0 => 0.5,
                    1 => (n / 2) as f64 + 0.5,
                    _ => ((i * 17 + j * 11) as i32 % n) as f64 + 0.35,
                };
                let angle = if j % 3 == 0 { -0.07 } else { 0.045 };
                if pair {
                    let height = match j {
                        2 | 5 => n as f64 + 2.0,
                        1 | 3 => y * 0.2,
                        _ => y * 0.67,
                    };
                    let a = Motion {
                        position: Vec2(-(n as f64) - 7.0, height),
                        delta: Vec2(11.0 + j as f64, if j % 2 == 0 { -1.0 } else { 1.0 }),
                        angle: 0.035,
                        turn: if rotates { 0.015 } else { 0.0 },
                    };
                    let b = Motion {
                        position: Vec2::default(),
                        delta: Vec2(0.5, -0.1),
                        angle,
                        turn: if rotates { -0.02 } else { 0.0 },
                    };
                    Trace::Pair(grid_pair::Trace { a, b })
                } else {
                    let start = if j == 5 {
                        Vec2(-5.0, n as f64 + 7.0)
                    } else {
                        Vec2(-5.0, y)
                    };
                    let delta = Vec2(n as f64 + 11.0, if j % 2 == 0 { -0.6 } else { 0.7 });
                    let target = Motion {
                        position: Vec2::default(),
                        delta: Vec2(0.3, 0.02),
                        angle,
                        turn: if rotates { 0.05 } else { 0.0 },
                    };
                    Trace::Point(point::Trace {
                        start,
                        delta,
                        target,
                    })
                }
            });
            Job {
                a,
                b,
                edit,
                queries,
            }
        })
        .collect();
    (ships, jobs)
}

fn measure<const C: bool>(
    scale: &Scale,
    pair: bool,
    damage: &str,
    ships: &[Vec<Cell>],
    jobs: &[Job],
    backend: Backend,
    profile: &str,
) {
    let mut counts = Counts::default();
    let mut outcomes = Outcomes::default();
    let mut samples = Vec::new();
    let mut builds = Vec::new();
    let size = Cell(scale.dimension, scale.dimension);
    for job in jobs {
        let start_build = (!C).then(Instant::now);
        let a = Shape::new(backend, size, if pair { &ships[job.a] } else { &[] });
        let mut b = Shape::new(backend, size, &ships[job.b]);
        if let Some(start) = start_build {
            builds.push(start.elapsed().as_nanos() as u64);
        }
        // Warm the same shapes before each operation. These calls are uncounted.
        black_box(job.queries[7].run::<false>(&a, &b, &mut Counts::default()));
        let start = (!C).then(Instant::now);
        for &cell in &job.edit {
            b.erase::<C>(cell, &mut counts);
        }
        let answers = job
            .queries
            .map(|q| black_box(q.run::<C>(&a, &b, &mut counts)));
        if let Some(start) = start {
            samples.push(start.elapsed().as_nanos() as u64);
        }
        for answer in &answers {
            outcomes.record(answer);
        }
    }
    println!(
        "{}",
        serde_json::json!({
            "scale":scale.name,"kind":if pair { "pair" } else { "point" },"damage":damage,
            "backend":backend.name(),"profile":profile,"batches":jobs.len(),"queries":jobs.len()*8,"outcomes":outcomes,
            "mode": if C {"count"} else {"time"}, "counts": if C {Some(counts)} else {None},
            "total_ns":samples,"build_ns":builds
        })
    );
}

fn verify_workload(scale: &Scale, pair: bool, damage: &str, linear: bool) {
    let profile = if linear { "linear" } else { "mixed" };
    let (ships, jobs) = workload(scale, pair, damage, linear);
    let size = Cell(scale.dimension, scale.dimension);
    let reference = if pair { Backend::Fixed8 } else { Backend::Dda };
    for (batch, job) in jobs.iter().enumerate() {
        let a_cells: &[Cell] = if pair { &ships[job.a] } else { &[] };
        let b_cells = &ships[job.b];
        let shape = |backend| {
            let a = Shape::new(backend, size, a_cells);
            let mut b = Shape::new(backend, size, b_cells);
            for &c in &job.edit {
                b.erase::<false>(c, &mut Counts::default());
            }
            (a, b)
        };
        let (a, b) = shape(reference);
        let wanted = job
            .queries
            .map(|q| q.run::<false>(&a, &b, &mut Counts::default()));
        for backend in Backend::ALL {
            if backend == reference || (pair && backend == Backend::Dda) {
                continue;
            }
            let (a, b) = shape(backend);
            for (i, (trace, expected)) in job.queries.iter().zip(&wanted).enumerate() {
                let got = trace.run::<false>(&a, &b, &mut Counts::default());
                let tolerance = if trace.rotating() {
                    ROT_EPS * 5.0
                } else {
                    TIME_EPS
                };
                assert!(
                    same(expected, &got, tolerance),
                    "{} {} {damage} batch {batch} query {i}: {expected:?} vs {got:?}",
                    scale.name,
                    backend.name()
                );
            }
        }
    }
    println!(
        "{}",
        serde_json::json!({
        "kind":if pair { "pair" } else { "point" }, "scale":scale.name, "damage":damage,"profile":profile,
            "batches":jobs.len(),"queries":jobs.len()*8
        })
    );
}

fn bench<const C: bool>(config: &Config, kind: &str, offset: usize, profile: &str) {
    for pair in [false, true] {
        if kind != "all" && kind != if pair { "pair" } else { "point" } {
            continue;
        }
        for scale in &config.scales {
            for damage in DAMAGE {
                for (name, linear) in [("mixed", false), ("linear", true)] {
                    if profile != "all" && profile != name {
                        continue;
                    }
                    let (ships, jobs) = workload(scale, pair, damage, linear);
                    for i in 0..Backend::ALL.len() {
                        let backend = Backend::ALL[(i + offset) % Backend::ALL.len()];
                        if pair && backend == Backend::Dda {
                            continue;
                        }
                        measure::<C>(scale, pair, damage, &ships, &jobs, backend, name);
                    }
                }
            }
        }
    }
}

fn main() {
    let config: Config = serde_json::from_str(include_str!("../cases.json")).unwrap();
    let mut args = std::env::args().skip(1);
    let mode = args.next().unwrap_or_else(|| "verify".to_owned());
    let kind = args.next().unwrap_or_else(|| "all".to_owned());
    let offset = args
        .next()
        .map_or(0, |arg| arg.parse().expect("numeric backend offset"));
    let profile = args.next().unwrap_or_else(|| "all".to_owned());
    assert!(matches!(kind.as_str(), "all" | "point" | "pair"));
    assert!(matches!(profile.as_str(), "all" | "mixed" | "linear"));
    match mode.as_str() {
        "verify" => verify(&config),
        "count" => bench::<true>(&config, &kind, offset, &profile),
        "time" => bench::<false>(&config, &kind, offset, &profile),
        _ => panic!(
            "usage: grid-collision-benchmark [verify|count|time] [all|point|pair] [backend-offset]"
        ),
    }
}
