use crate::grid::{Counts, Grid, Rect};
use std::cmp::Ordering;

#[derive(Clone, Copy)]
pub enum Kind {
    Block(usize),
    Branch(usize, usize),
}
pub struct Node {
    pub bounds: Option<Rect>,
    pub kind: Kind,
    parent: Option<usize>,
}
pub struct Bvh {
    pub nodes: Vec<Node>,
    pub root: Option<usize>,
    leaves: Vec<Option<usize>>,
}
impl Bvh {
    pub fn new(grid: &Grid) -> Self {
        let mut tree = Self {
            nodes: Vec::new(),
            root: None,
            leaves: vec![None; grid.capacity()],
        };
        let mut blocks: Vec<_> = grid.active_blocks().collect();
        if !blocks.is_empty() {
            tree.root = Some(tree.build(grid, &mut blocks));
        }
        tree
    }
    fn build(&mut self, grid: &Grid, ids: &mut [usize]) -> usize {
        let bounds = ids
            .iter()
            .map(|&id| grid.rect(id))
            .reduce(Rect::union)
            .unwrap();
        let kind = if ids.len() == 1 {
            self.leaves[ids[0]] = Some(self.nodes.len());
            Kind::Block(ids[0])
        } else {
            let half = bounds.half();
            ids.sort_unstable_by_key(|&id| {
                let cell = grid.block_coord(id);
                if half.0 >= half.1 {
                    (cell.0, cell.1)
                } else {
                    (cell.1, cell.0)
                }
            });
            let (left, right) = ids.split_at_mut(ids.len() / 2);
            let l = self.build(grid, left);
            let r = self.build(grid, right);
            let parent = self.nodes.len();
            self.nodes[l].parent = Some(parent);
            self.nodes[r].parent = Some(parent);
            Kind::Branch(l, r)
        };
        let i = self.nodes.len();
        self.nodes.push(Node {
            bounds: Some(bounds),
            kind,
            parent: None,
        });
        i
    }
    pub fn empty_block<const C: bool>(&mut self, block: usize, counts: &mut Counts) {
        let mut node = self.leaves[block];
        while let Some(i) = node {
            let bounds = match self.nodes[i].kind {
                Kind::Block(_) => None,
                Kind::Branch(a, b) => match (self.nodes[a].bounds, self.nodes[b].bounds) {
                    (Some(a), Some(b)) => Some(a.union(b)),
                    (a, b) => a.or(b),
                },
            };
            if self.nodes[i].bounds == bounds {
                break;
            }
            if C {
                counts.index_nodes_updated += 1;
            }
            self.nodes[i].bounds = bounds;
            node = self.nodes[i].parent;
        }
    }
}

#[derive(Clone, Copy)]
pub struct Candidate(pub f64, pub usize, pub usize);
impl Ord for Candidate {
    fn cmp(&self, rhs: &Self) -> Ordering {
        rhs.0
            .total_cmp(&self.0)
            .then_with(|| (rhs.1, rhs.2).cmp(&(self.1, self.2)))
    }
}
impl PartialOrd for Candidate {
    fn partial_cmp(&self, rhs: &Self) -> Option<Ordering> {
        Some(self.cmp(rhs))
    }
}
impl PartialEq for Candidate {
    fn eq(&self, rhs: &Self) -> bool {
        self.cmp(rhs) == Ordering::Equal
    }
}
impl Eq for Candidate {}
