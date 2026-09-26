# Design decisions

Decisions that were measured or argued rather than obvious, newest first. Each records what was chosen, the
evidence, and what would make us revisit it.

## 2026-09-25: Compare window: the ranker decides "better", the page's own view-models, previews through an interface

**"Better" comes from `KeeperRanker.Compare`, not from rules in the window.** Each detail row is marked only when its
own ranking criterion separates the two photos (resolution tier, camera data, copy-like name, size within one format,
older copy), and the verdict is the ranker's reason. So the window can't praise the photo the ranking put second.
Comparing correctly needs the same context the ranking used: the grouper ranks each group against the whole connected
set, and "within 1% of the largest" depends on it. `PhotoGroup.RankContext` carries that set (`RemainingGroups` keeps
it after moves). A test checks `Compare` against `Rank` for every pair. *Known gap:* a photo that undo put back under a
new name ("a (2).jpg") is compared under that name, while the group's order came from the scan-time name. The verdict
is then right about the files as they are now, and can differ from the order.

**The same objects as the results page, not copies.** The panes hold the page's `PhotoViewModel`s, and selecting goes
through their `ToggleCommand`, so the keep-one rule, the totals and closing the confirmation all behave exactly as a
click on the tile. Nothing needs syncing. The cost is that the page rebuilds its rows after every move, undo and
re-group. The window then finds its place again by path: the same photo, or failing that its group, or failing that the
group now at the same position, and closes when nothing is left.

**Previews through `IPreviewLoader`, returning `object`.** Presentation can't name WPF's `ImageSource`, and the
interesting logic (don't open online-only files, cancel when moving on, drop late results, prefetch neighbours, don't
reload when the pane grows 10% or the preview is already the whole photo) belongs in the view-model where it's tested.
Only decoding and caching are in Desktop. Actual-size decodes aren't cached or prefetched, because one 48 MP photo is
about 190 MB.

**Pan is shared as a fraction (0–1) of each picture**, not in pixels, so two photos of different resolutions still show
the same spot. Zoom 1 means one image pixel per screen pixel (the behaviour divides out Windows display scaling).

**Opening it never changes the selection.** A tile click selects, so double-click was rejected (it would toggle twice).
Instead: a Compare button on the tile (shown on hover and keyboard focus), right-click > Compare, and Enter on a
focused tile. There's also a Compare button on each group row.

## 2026-09-25: The results page works out what to show from the scan; undo is a stack that lasts as long as the page

**Worked out, not edited in place.** After a move the page doesn't delete rows. It keeps the scan paths moved to the
review folder and, for photos undo put back as "name (2).jpg", their new paths. The rows are always
`RemainingGroups.Apply(groups for the level, moved out, renamed)`: moved photos left out, groups with fewer than two
photos dropped. Moves, undos (in any order) and strictness changes then all go through one pure function. Editing rows
in place would break on the first re-group, because the grouper works from the scan, which still lists the moved
photos. Selection is carried over by path, and photos put back by undo come back selected.

**When the suggested keeper is moved** (the user kept other copies instead), the best-ranked photo left stands in as
keeper. That group suggests nothing, because the other members' match kinds were measured against the old keeper, not
the new one. *Alternative:* re-rank or re-group what's left. Rejected for now, because it could reshuffle groups the user
is working through.

**What stays selected after a failed move.** Only "in use by another app" (`CleanupFailureReason.InUse`, split out of
`IoError` in Core from `ERROR_SHARING_VIOLATION`/`ERROR_LOCK_VIOLATION`) stays selected, because clicking Move again
after closing that app will work. Changed since the scan, missing, kept copy missing and other I/O errors are deselected
and the page says to scan again, because a retry would fail the same way.

**Undo stack, this page only.** Each move is a batch; the undo bar offers the latest, then the one before. Leaving the
page drops the stack (files stay in `_PhotoSweep Removed`), so Back asks first when something could still be undone.
`ReviewFolder.FindBatches` can already find batches on disk, but there's no UI for it yet (see polish list).

**Closing mid-move waits** for the batch to finish rather than cancelling it. The write-ahead manifest would make a
crash safe, but finishing leaves nothing for the next undo to tidy up.

**Out of scope for now:** undoing batches from earlier sessions (`FindBatches`), and moving Takeout `.json` sidecars
with their photos (see the open issue below).

## 2026-09-25: Results-page thumbnails: ImageSharp, checked for cloud files twice, settle delay before decoding

**ImageSharp, not WIC.** WPF's own decoder (WIC) is faster and shows HEIC when the Windows codec is installed, but
rule 4 says ImageSharp decodes everything. `Core/Imaging/Thumbnail` sets `DecoderOptions.TargetSize`, so JPEGs are
scaled down *while* decoding, applies `AutoOrient()` (WPF ignores EXIF orientation), and returns raw BGRA pixels
that Desktop wraps in a frozen `BitmapSource`. *Trade-off:* HEIC shows "No preview". `TargetSize` also scales small
images *up*, so it's only set when the image is bigger than the box (a test caught this).

**Two online-only checks.** The view-model re-checks each photo's attributes every time its tile comes on screen,
so a file freed up by OneDrive after the scan gets the cloud placeholder. `Thumbnail.LoadAsync` checks again right
before opening the file, because a thumbnail can wait in the decode queue while the file changes. The second check
is the one rule 6 relies on; the first decides what the tile shows.

**Settle delay.** A scroll test (1,500 groups, 3,000 photos, the scrollbar dragged top to bottom in about 2 s,
Debug counters from `ThumbnailStats`) measured:

| | requested | skipped | decoded | decoded too late for their row |
|---|---|---|---|---|
| Cancel-while-queued only | 912 | 87 | 825 | 278 |
| + 60 ms settle delay + cancellable decode | 890 | 884 | 6 | 0 |

While dragging, a row is on screen for about 20 ms, so a 60 ms wait before joining the queue skips it for free.
Scrolling a page at a time (2 pages/s) still decoded every thumbnail and skipped none. Revisit if thumbnails feel
slow to appear on very fast machines, where the delay becomes most of the wait.

## 2026-09-25: Pre-selection lives in one policy; at Similar only byte-identical copies are pre-selected

`Core/Cleanup/PreselectionPolicy.IsSuggested(member, level)` is the only place that decides what starts out ticked:
every non-keeper at Exact and SamePhoto, and at Similar only members byte-identical to the keeper (a Similar group can
hold different burst frames the user wants to keep). It takes the whole `GroupMember`, so tuning can use the hash
distances later without touching the page. It never suggests the keeper, so the policy can't select a whole group.
The page enforces that rule for clicks too (the last unselected photo can't be selected), and `CleanupPlan`
checks it again before anything moves.

## 2026-09-25: Strictness travels next to ScanOptions, not inside it

The start screen hands over a `ScanRequest(ScanOptions Options, MatchLevel Level)` (Presentation) instead of adding
`MatchLevel` to `ScanOptions` (Core). The scan doesn't depend on the level: every file gets the same hashes, and they are
cached. Only `DuplicateGrouper.Group(scan, level)` uses it. Keeping it out of `ScanOptions` means the scanner never
carries a setting it ignores, and a later results screen can re-group at another level without scanning again.
*Trade-off:* one more small type to pass around. Revisit if some level ever needs different scan work (e.g. Exact
skipping perceptual hashing to save time), because then the scanner would genuinely need to know the level.

## 2026-09-25: Clean-up writes the manifest before each move, moves without copying, and never deletes recursively

**Write-ahead manifest.** Each file's manifest entry is saved (temp file, then swapped in) *before* the file is moved,
and removed again if the move fails. If the manifest can't be written, the file isn't moved. Writing the entry *after*
the move would leave a window where a crash loses track of a moved file. With the entry first, a crash can only
leave an entry for a file that never moved, and undo can spot that: the review copy is missing and the original is still
there with the recorded size and last-write time, so the entry is dropped. Entries store paths relative to the
root, and the root comes from where the manifest sits, so a batch still undoes if a drive letter changes.
*Trade-off:* the whole manifest is rewritten per file, which is O(n²) bytes: about 100 MB of writes for a 1,000-file
batch. Revisit with append-only JSON Lines if batches of tens of thousands become normal.

**No-copy move.** `File.Move` passes `MOVEFILE_COPY_ALLOWED` on Windows, so a move to another volume (a mount point
inside a scanned folder) silently becomes a copy, which reads every byte and downloads online-only files. Clean-up
calls `MoveFileExW` with no flags through a source-generated `[LibraryImport]` instead, so that case fails with
`ERROR_NOT_SAME_DEVICE` and is reported. It also never overwrites a destination, so undo can detect a taken
name atomically instead of by check-then-move. *Verified:* with a handle open that shares only `FileShare.Delete`,
Windows refuses other reads (sharing violation) but allows the rename. The tests use that to prove an `Offline` file
is moved and restored without being read. *Not tested automatically:* the cross-volume refusal (needs a second volume).

**Non-recursive clean-up.** After a full undo, the manifest (our own file, deleted only once it lists nothing) and
then each empty folder are removed with non-recursive `Directory.Delete`, deepest first. Windows refuses to remove a
folder that still holds anything, so a bug in "is it empty?" can at worst leave a folder behind, never delete a photo.

**Re-checked at execution time.** Validation (`CleanupPlan.Validate`) refuses selections that remove every copy
in a group. Just before moving, each group is checked again: at least one kept member must still exist with the
size and last-write time from the scan (an edited keeper doesn't count), otherwise the group is skipped. Each selected
file must also still match the scan, otherwise it's skipped as changed. These checks read directory metadata only.

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

### 2026-09-25: Hand review of SamePhoto groups (Google Takeout library)

**Setup.** A Google Takeout export of about 9,000 photos (8,998 decoded; 540 SamePhoto groups covering 1,450 files)
under the current limits (SamePhoto: pHash ≤ 8, dHash ≤ 8). 34 groups were judged by eye on a throwaway review page:
30 picked at random with a fixed seed, plus 4 flagged beforehand as suspicious.

**Judging rule.** "Same" meant no visible difference at all. Pairs marked "different" were very close but had a
visible change: facial pose or expression, lighting, or a small shift in the scene. This is why burst frames appear
on both sides of the tally: frames where nothing visibly changed were judged same, frames with any change different.
It also defines the levels for tuning:
- **SamePhoto** = visually indistinguishable: a re-save, resize or re-compress of the same picture.
- **Similar** = near-identical shots with any real change (bursts, retakes). Shown to the user, never pre-selected.

**Result.** 20 same photo, 14 different photo, 0 wrong keeper. Precision on the random 30 alone: 19/30 ≈ 63%.
The library has no byte-identical copies, so every group was a look-alike match.

**What went wrong (14 groups).**
- 7 screenshots of the same app (Pokémon GO, YouTube, authenticator). This confirms the screenshot issue below.
- 6 burst frames taken 2–3 s apart.
- 1 burst cover GIF grouped with one of the burst's stills.

**Where the line falls.** Every wrong group had pHash ≥ 5 or dHash ≥ 5. True re-saves (a photo vs a screenshot of
it, Snapchat and WhatsApp re-saves) were at 0–1 bits.

**Candidates for tuning (no code changes yet; tuning comes after the UI).**
- SamePhoto = pHash ≤ 4 **and** dHash ≤ 4. On this sample it keeps 14 of the 20 correct groups and none of the 14
  wrong ones. The 6 correct groups it loses are burst frames with no visible change; under the definitions above,
  burst frames belong in Similar anyway.
- Screenshots need a stricter rule than photos, and should never be pre-selected for removal.
- Burst cover GIFs shouldn't be grouped with the burst's stills.
- Caveat: 34 groups from one library. Re-check the candidate limits on a second library (e.g. the 17k OneDrive one)
  before adopting them.

**Related: Takeout `.json` sidecars.** Each Takeout photo has a metadata sidecar (`IMG_1234.jpg.json`). Clean-up
moves only the photo, so its sidecar stays behind in the library. Decide whether sidecars should move with their photo
(and back on undo).

- **Lock-screen and app screenshots group as SamePhoto.** Screenshots of the same lock screen or app UI differ
  only in small text (clock, notifications) and hash 4–5 bits apart, well inside the SamePhoto limits. The hashes
  are doing their job, as the images really are ~95% identical pixels, but they aren't duplicates. Tighter
  thresholds won't separate them without breaking real matches; this likely needs screenshot-specific handling.
  The hand review above found 7 of its 14 wrong groups were app screenshots.
- **`name.0.jpg` isn't recognised as a copy name.** Some apps save a second copy as `X.0.jpg`, and it can win the
  keeper ranking on age. Adding a `\.\d+$` pattern is cheap but could match real file names, so it needs testing
  against a real library first.

## Polish list

- **Restore earlier clean-ups (`FindBatches`).** Undo only covers batches moved on the current results page. A
  "Review folder" page could list every batch still in `_PhotoSweep Removed` (via `ReviewFolder.FindBatches`) and undo
  any of them.
- **Remember the compare window's size and position.** It opens at 1200×840, centred on the main window, every time.
  Saving the bounds (and maximised state) to a small settings file, and checking they're still on a connected screen
  before using them, would let it reopen where the user left it.
- **A keyboard shortcut for 1:1.** WPF key bindings can't use a bare letter or digit, so the 1:1 mode currently only
  has its button. Ctrl+1 or F11-style keys are candidates.
