# Design decisions

Decisions that were measured or argued rather than obvious, newest first. Each records what was chosen, the
evidence, and what would make us revisit it.

## 2026-09-25: Groups are split around a keeper, not taken whole from union-find

**Decision.** After union-find joins every matching pair, each connected set is split: the best-ranked file
becomes a keeper and takes every file within the level's thresholds of *it*; the leftovers are split the same way
until fewer than two remain. Byte-identical copies are decided together (as one distinct file), so they are never
split apart.

**Why.** Matching isn't transitive. In a burst, each frame matches the next but the first and last frames can be
far apart. On a real 17,466-photo library, plain union-find produced a 67-photo "SamePhoto" group whose members were
up to 16/16 bits from the keeper, too big to review and not "the same photo". After splitting, every member of a
group is directly within the thresholds of its keeper.

**Trade-off.** Greedy: a file that only matches a member already taken by an earlier keeper is dropped rather than
grouped (A~B, B~C, keeper A takes B, C is left alone). This loses a few real pairs in exchange for groups that are
honest about what they contain. The result is deterministic: the set is ranked once, independent of input order,
and keepers are taken in rank order.

## 2026-09-25: Look-alike search is brute force, not a BK-tree

**Decision.** The look-alike pass compares every pair of distinct fingerprints in flat `ulong[]` arrays
(two XORs and two popcounts per pair). The BK-tree that was built first has been removed.

**Evidence.** Real library of 17,466 photos (15,070 distinct fingerprints), Release build, 12 cores:

| Approach                                  | Time    | Matches |
|-------------------------------------------|---------|---------|
| BK-tree, radius 8 queries (pHash)          | 2.2 s   | 27,344  |
| Brute force, pHash only, all ordered pairs | 0.13 s  | 27,344  |
| Brute force as shipped (both hashes, each pair once) | 85 ms | 4,490 pairs |
| Same, `Parallel.For` over 12 cores         | 35 ms   | 4,490 pairs |

**Why the tree lost.** Not because of the data: a radius-8 query visited 51% of the tree on the real hashes and
52% on uniformly random ones (mean pairwise distance 31.4 vs 32.0). The cause is the metric. Distances between
64-bit hashes concentrate around 32 (±4), so a node's children sit on edges of roughly 24–40, and a query at
distance d≈32 must descend every edge in [d−8, d+8], which is nearly all of them. Each query therefore touches about
half the tree, and each node visit (dictionary enumeration, stack push, pointer chase) costs far more than one
popcount on a contiguous array. BK-trees shine with small radii relative to the distance spread; radius 8 of 64
isn't small enough.

**Not parallelised.** `Parallel.For` cut the pair loop from 85 ms to 35 ms, but the whole grouping step takes
166 ms and runs after a scan of 0.4 s (cached) to minutes (first time), so the saving isn't noticeable. It would
also need per-thread pair lists, because union-find isn't thread-safe. Brute force is O(n²): revisit (parallelise
first, then consider multi-index hashing) if libraries of ~50,000+ distinct photos become a target, where the
sequential loop would pass one second.

## Open issues (for the threshold-tuning phase)

- **Lock-screen and app screenshots group as SamePhoto.** Screenshots of the same lock screen or app UI differ
  only in small text (clock, notifications) and hash 4–5 bits apart, well inside the SamePhoto limits. The hashes
  are doing their job, as the images really are ~95% identical pixels, but they aren't duplicates. Tighter
  thresholds won't separate them without breaking real matches; this likely needs screenshot-specific handling.
- **`name.0.jpg` isn't recognised as a copy name.** Some apps save a second copy as `X.0.jpg`, and it can win the
  keeper ranking on age. Adding a `\.\d+$` pattern is cheap but could match real file names, so it needs testing
  against a real library first.
