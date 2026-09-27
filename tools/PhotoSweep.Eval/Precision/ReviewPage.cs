using System.Globalization;
using System.Net;
using System.Text;

namespace PhotoSweep.Eval.Precision;

/// <summary>A pair's thumbnail: a URL relative to the page, or the reason there isn't one.</summary>
public sealed record ThumbInfo(string? Url, string? Missing);

/// <summary>
/// Writes the labelling page: one card per sampled pair (keeper and member side by side), grouped by bucket, with
/// Same/Different buttons. The <see cref="LabelFile"/> is embedded as JSON; the page's script fills in verdicts, keeps
/// them in localStorage so a reload loses nothing, and "Export labels" downloads the file for the precision command.
/// </summary>
/// <remarks>
/// A static file opened from disk rather than a local web server: nothing to keep running, and a Blob download is all
/// saving needs. The script is the only logic in the page and is deliberately small (set, restore, count, export, load).
/// </remarks>
public static class ReviewPage
{
    public static string Render(LabelFile file, IReadOnlyList<(BucketInfo Bucket, CandidatePair Pair)> sample, IReadOnlyDictionary<string, ThumbInfo> thumbs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Pair Review</title>");
        sb.AppendLine(Style);
        sb.AppendLine("<header>");
        sb.AppendLine(Inv($"  <h1>Pair review: {sample.Count} pairs, grouped at pHash ≤ {file.GroupedAtPHash}, dHash ≤ {file.GroupedAtDHash}</h1>"));
        sb.AppendLine("  <p class=\"rule\"><b>Same</b> = no visible difference at all (a re-save, resize or re-compress). Any visible change (pose, expression, light, framing, on-screen text) = <b>Different</b>. Keys: <kbd>S</kbd> / <kbd>D</kbd> on a focused pair, then it moves on.</p>");
        sb.AppendLine("  <div class=\"bar\"><div class=\"tally\" id=\"tally\"></div><div class=\"actions\">");
        sb.AppendLine("    <button id=\"export\" class=\"primary\">Export labels</button>");
        sb.AppendLine("    <label class=\"load\">Load labels<input type=\"file\" id=\"load\" accept=\".json,application/json\"></label>");
        sb.AppendLine("  </div></div>");
        sb.AppendLine("  <div class=\"status\" id=\"status\" role=\"status\"></div>");
        sb.AppendLine("</header>");
        sb.AppendLine("<main>");

        var labelById = file.Pairs.ToDictionary(p => (p.KeeperPath, p.MemberPath), p => p.Id);
        foreach (var bucket in file.Buckets)
        {
            var inBucket = sample.Where(s => s.Bucket.Name == bucket.Name).ToList();
            sb.AppendLine(Inv($"<h2 class=\"bucket\">Bucket {H(bucket.Name)} bits <span>{inBucket.Count} of {bucket.Population} pairs</span></h2>"));
            foreach (var (_, pair) in inBucket)
            {
                var id = labelById[(pair.Keeper.Path, pair.Member.Path)];
                sb.AppendLine(Inv($"<section class=\"pair\" id=\"pair-{id}\" data-id=\"{id}\" tabindex=\"0\">"));
                sb.AppendLine("  <div class=\"head\">");
                sb.AppendLine(Inv($"    <h3>#{id}</h3><span class=\"dist\">pHash {pair.PHash} · dHash {pair.DHash}</span>"));
                sb.AppendLine("    <div class=\"buttons\"><button class=\"b-same\" data-verdict=\"same\" aria-pressed=\"false\">Same</button><button class=\"b-diff\" data-verdict=\"different\" aria-pressed=\"false\">Different</button></div>");
                sb.AppendLine("  </div>");
                sb.AppendLine("  <div class=\"photos\">");
                AppendFigure(sb, file.Folder, pair.Keeper, thumbs, isKeeper: true);
                AppendFigure(sb, file.Folder, pair.Member, thumbs, isKeeper: false);
                sb.AppendLine("  </div>");
                sb.AppendLine("</section>");
            }
        }

        sb.AppendLine("</main>");
        // System.Text.Json escapes <, > and & by default, so the embedded JSON can't close the script element early.
        sb.Append("<script type=\"application/json\" id=\"labels\">").Append(file.ToJson()).AppendLine("</script>");
        sb.AppendLine(Script);
        return sb.ToString();
    }

    private static void AppendFigure(StringBuilder sb, string folder, Core.Scanning.ScannedFile file, IReadOnlyDictionary<string, ThumbInfo> thumbs, bool isKeeper)
    {
        var link = new Uri(file.Path).AbsoluteUri;
        var relative = Path.GetRelativePath(folder, file.Path);
        var dims = file.Details is { } d ? Inv($"{d.Width}×{d.Height}") : "size unknown";
        var picture = thumbs.TryGetValue(file.Path, out var t) && t.Url is { } url
            ? $"<img src=\"{H(url)}\" alt=\"\" loading=\"lazy\">"
            : $"<div class=\"nothumb\">{H(t?.Missing ?? "no preview")}</div>";

        sb.AppendLine(isKeeper ? "    <figure class=\"keeper\">" : "    <figure>");
        sb.AppendLine($"      <a href=\"{H(link)}\" target=\"_blank\">{picture}</a>");
        sb.AppendLine($"      <figcaption><span class=\"role\">{(isKeeper ? "Keeper" : "Member")}</span> <span class=\"name\">{H(Path.GetFileName(file.Path))}</span><br>");
        sb.AppendLine(Inv($"        <span class=\"meta\">{H(Path.GetDirectoryName(relative) ?? "")}<br>{dims} · {file.SizeBytes / 1024.0:0.#} KB · {file.LastWriteUtc:yyyy-MM-dd HH:mm}</span></figcaption>"));
        sb.AppendLine("    </figure>");
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private const string Style = """
        <style>
          :root {
            --bg: #f6f5f2; --card: #fff; --text: #1d1d1b; --muted: #6b6a65; --line: #e2e0da;
            --same: #1f7a4d; --diff: #b3261e; --keeper: #8a5a00; --accent: #2f5bd3;
            color-scheme: light;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #161614; --card: #1f1f1d; --text: #ecebe6; --muted: #9c9a93; --line: #34332f;
              --same: #5cc28f; --diff: #f07a72; --keeper: #e0b04f; --accent: #8aa8ff;
              color-scheme: dark;
            }
          }
          body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.4 system-ui, "Segoe UI", sans-serif; }
          header { position: sticky; top: 0; z-index: 1; background: var(--bg); border-bottom: 1px solid var(--line); padding: 12px 24px; }
          h1 { font-size: 18px; margin: 0 0 4px; }
          .rule { margin: 0 0 8px; color: var(--muted); font-size: 13px; }
          kbd { font: 12px ui-monospace, Consolas, monospace; border: 1px solid var(--line); border-radius: 4px; padding: 0 4px; }
          .bar { display: flex; flex-wrap: wrap; gap: 8px 20px; align-items: center; }
          .tally { display: flex; flex-wrap: wrap; gap: 4px 18px; font-variant-numeric: tabular-nums; font-size: 13px; }
          .tally b { font-size: 15px; }
          .actions { margin-left: auto; display: flex; gap: 8px; align-items: center; }
          .status { color: var(--accent); font-size: 13px; min-height: 1.4em; }
          main { padding: 8px 24px 48px; display: grid; gap: 14px; }
          h2.bucket { font-size: 16px; margin: 18px 0 0; }
          h2.bucket span { color: var(--muted); font-weight: 400; font-size: 13px; margin-left: 8px; }
          .pair { background: var(--card); border: 1px solid var(--line); border-left: 4px solid var(--line); border-radius: 8px; padding: 12px 16px; outline-offset: 2px; }
          .pair:focus { outline: 2px solid var(--accent); }
          .pair.same { border-left-color: var(--same); }
          .pair.diff { border-left-color: var(--diff); }
          .head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px 14px; margin-bottom: 10px; }
          .head h3 { font-size: 15px; margin: 0; }
          .dist { font-variant-numeric: tabular-nums; color: var(--muted); }
          .buttons { margin-left: auto; display: flex; gap: 6px; }
          button, .load { font: inherit; font-size: 13px; padding: 5px 10px; border-radius: 6px; border: 1px solid var(--line); background: transparent; color: var(--text); cursor: pointer; }
          button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
          .load input { display: none; }
          button[aria-pressed="true"].b-same { background: var(--same); border-color: var(--same); color: #fff; }
          button[aria-pressed="true"].b-diff { background: var(--diff); border-color: var(--diff); color: #fff; }
          .photos { display: flex; flex-wrap: wrap; gap: 14px; }
          figure { margin: 0; width: min(420px, 100%); }
          figure img, .nothumb { width: 100%; aspect-ratio: 1; object-fit: contain; background: var(--bg); border-radius: 4px; display: block; }
          .nothumb { display: grid; place-items: center; color: var(--muted); font-size: 12px; }
          figcaption { font-size: 12px; margin-top: 6px; overflow-wrap: anywhere; }
          .role { font-weight: 600; color: var(--muted); }
          .keeper .role { color: var(--keeper); }
          .name { font-weight: 600; }
          .meta { color: var(--muted); }
        </style>
        """;

    private const string Script = """
        <script>
        const data = JSON.parse(document.getElementById('labels').textContent);
        const byId = new Map(data.pairs.map(p => [p.id, p]));
        const storeKey = `photosweep-eval:${data.folder}|${data.seed}|${data.groupedAtPHash}/${data.groupedAtDHash}|${data.pairs.length}`;
        const status = msg => { document.getElementById('status').textContent = msg; };

        function save() {
          const verdicts = Object.fromEntries(data.pairs.filter(p => p.verdict).map(p => [p.id, p.verdict]));
          try { localStorage.setItem(storeKey, JSON.stringify(verdicts)); } catch { /* private window: labels still export */ }
        }

        function restore() {
          let saved = {};
          try { saved = JSON.parse(localStorage.getItem(storeKey) || '{}'); } catch { }
          for (const [id, v] of Object.entries(saved)) {
            const p = byId.get(Number(id));
            if (p && (v === 'same' || v === 'different')) p.verdict = v;
          }
        }

        function show(p) {
          const el = document.getElementById('pair-' + p.id);
          el.classList.toggle('same', p.verdict === 'same');
          el.classList.toggle('diff', p.verdict === 'different');
          for (const b of el.querySelectorAll('button[data-verdict]')) b.setAttribute('aria-pressed', String(b.dataset.verdict === p.verdict));
        }

        function tally() {
          const parts = data.buckets.map(b => {
            const ps = data.pairs.filter(p => p.bucket === b.name);
            const same = ps.filter(p => p.verdict === 'same').length, diff = ps.filter(p => p.verdict === 'different').length;
            return `<span>${b.name}: <b>${same + diff}</b>/${ps.length} (${same} same, ${diff} diff)</span>`;
          });
          const done = data.pairs.filter(p => p.verdict).length;
          document.getElementById('tally').innerHTML = `<span>Labelled <b>${done}</b> of ${data.pairs.length}</span>` + parts.join('');
        }

        function setVerdict(id, verdict, toggle) {
          const p = byId.get(id);
          p.verdict = toggle && p.verdict === verdict ? null : verdict;
          show(p); tally(); save();
        }

        document.addEventListener('click', e => {
          const b = e.target.closest('button[data-verdict]');
          if (b) setVerdict(Number(b.closest('.pair').dataset.id), b.dataset.verdict, true);
        });

        document.addEventListener('keydown', e => {
          const card = e.target.closest?.('.pair');
          if (!card || e.ctrlKey || e.altKey || e.metaKey) return;
          const verdict = { s: 'same', d: 'different' }[e.key.toLowerCase()];
          if (!verdict) return;
          e.preventDefault();
          setVerdict(Number(card.dataset.id), verdict, false);
          const next = [...document.querySelectorAll('.pair')].find(el => el.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_PRECEDING);
          if (next) { next.focus(); next.scrollIntoView({ block: 'start', behavior: 'smooth' }); }
        });

        document.getElementById('export').addEventListener('click', () => {
          const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
          const a = document.createElement('a');
          a.href = URL.createObjectURL(blob);
          a.download = `labels-seed${data.seed}-${new Date().toISOString().slice(0, 16).replace('T', '_').replace(':', '')}.json`;
          document.body.appendChild(a); a.click(); a.remove();
          setTimeout(() => URL.revokeObjectURL(a.href), 1000);
          const open = data.pairs.filter(p => !p.verdict).length;
          status(open ? `Exported. ${open} pairs still unlabelled (left out of precision).` : 'Exported all labels.');
        });

        document.getElementById('load').addEventListener('change', async e => {
          const f = e.target.files[0];
          e.target.value = '';
          if (!f) return;
          try {
            const loaded = JSON.parse(await f.text());
            if (loaded.version !== data.version || loaded.folder !== data.folder || loaded.seed !== data.seed)
              return status('Not loaded: that file is from a different sample (folder, seed or version differ).');
            let applied = 0;
            for (const lp of loaded.pairs || []) {
              const p = byId.get(lp.id);
              if (p && p.keeperPath === lp.keeperPath && p.memberPath === lp.memberPath && (lp.verdict === 'same' || lp.verdict === 'different' || lp.verdict === null)) {
                p.verdict = lp.verdict; show(p); if (lp.verdict) applied++;
              }
            }
            tally(); save();
            status(`Loaded ${applied} labels from ${f.name}.`);
          } catch (err) { status('Not loaded: ' + err.message); }
        });

        restore();
        data.pairs.forEach(show);
        tally();
        </script>
        """;
}
