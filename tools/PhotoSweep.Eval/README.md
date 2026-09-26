# PhotoSweep.Eval

Developer tool for measuring how well the perceptual-hash limits separate "same photo" from "different photo".
Not part of the app and never shipped. It only **reads** the photo folder, refuses an `--out` folder inside it,
and skips online-only files without opening them.

```powershell
# 1. Recall and false positives over a pHash/dHash grid (2–14 each), from generated variants of sampled photos.
dotnet run -c Release --project tools/PhotoSweep.Eval -- synthetic <folder> --out <dir> [--count 300] [--seed 20260926]

# 2. A labelling page for a stratified sample of keeper–member pairs from a real grouping.
dotnet run -c Release --project tools/PhotoSweep.Eval -- review <folder> --out <dir> [--p 8] [--d 8] [--per-bucket 30] [--cache <file>]

# 3. Precision from the labels the page exported.
dotnet run --project tools/PhotoSweep.Eval -- precision <labels.json> [--threshold 2|4|6|8] [--out <dir>]
```

**synthetic** writes seven variants per original to `<out>/variants` (resized 50% and 25%, JPEG q50 and q30, PNG,
EXIF orientation 6, 50% + q30), hashes everything with `Fingerprinter` exactly as the scanner does, and writes
`<out>/synthetic.md`: a recall grid, a false-positive grid (pairs of different originals), per-variant recall at
candidate limits, distance spreads, and the close "different original" pairs by path so they can be checked by eye
(a library's own bursts and copies show up there).

The variants are only needed while the run hashes them; the report doesn't link to them. They're big (full-size PNG
and JPEG copies): 1.9 GB for the 300-photo run on `sweep-test` (2026-09-26, in the scratchpad's `eval\variants`).
Delete `<out>/variants` once the report is written. A re-run with the same folder and seed regenerates them exactly.

**review** groups the folder at `--p/--d` and draws up to `--per-bucket` pairs from each bucket of
max(pHash, dHash): 0–2, 3–4, 5–6, 7–8. Byte-identical copies are left out. Open `<out>/review.html` in a browser,
mark each pair Same/Different (keys S/D), then **Export labels**. Labels also survive a reload (localStorage), and
**Load labels** resumes from an exported file.

**precision** reports precision per bucket, and overall at each threshold T (pHash ≤ T and dHash ≤ T) weighted by
each bucket's share of the whole grouping (recorded in the labels file), with 95% Wilson intervals. The overall
interval uses the Kish effective sample size, 1 / Σ(w²/n).
