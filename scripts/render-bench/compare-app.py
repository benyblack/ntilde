"""Compare in-app per-frame render metrics (NTILDE_RENDER_METRICS JSONL) of two builds.

Usage: python scripts/render-bench/compare-app.py <A.jsonl> <B.jsonl> [--min-dirty-rows N]

Only frames that actually repainted (DirtyRows >= N, default 1) are compared, so
idle/cursor-blink frames don't dilute the numbers. Busy frames (the top 50% by
DirtyCellsEstimated in A) are reported separately as the heavy-work signal.
"""
import json
import statistics as st
import sys

args = [a for a in sys.argv[1:] if not a.startswith("--")]
min_dirty = 1
if "--min-dirty-rows" in sys.argv:
    min_dirty = int(sys.argv[sys.argv.index("--min-dirty-rows") + 1])
pa, pb = args[0], args[1]


def load(p):
    rows = []
    for l in open(p, encoding="utf-8"):
        try:
            rows.append(json.loads(l))
        except ValueError:
            # The writer's last buffered line is cut off when the app exits.
            pass
    return [r for r in rows if r.get("DirtyRows", 0) >= min_dirty]


def pct(v, p):
    s = sorted(v)
    k = p / 100 * (len(s) - 1)
    lo, hi = int(k), min(int(k) + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


A, B = load(pa), load(pb)
if not A or not B:
    sys.exit(f"no repaint frames: A={len(A)} B={len(B)}")
cut = st.median([r["DirtyCellsEstimated"] for r in A])


def summary(rows):
    t = [r["FrameTimeMs"] for r in rows]
    return {
        "frames": len(rows),
        "median": st.median(t),
        "p95": pct(t, 95),
        "p99": pct(t, 99),
        "mean": st.mean(t),
        "dirty cells/frame": st.mean(r["DirtyCellsEstimated"] for r in rows),
        "alloc KB/frame": st.mean(r.get("AllocBytesThisFrame", 0) for r in rows) / 1024,
    }


for title, fa, fb in (
    ("all repaint frames", A, B),
    (f"busy frames (DirtyCellsEstimated >= {cut:.0f})",
     [r for r in A if r["DirtyCellsEstimated"] >= cut],
     [r for r in B if r["DirtyCellsEstimated"] >= cut]),
):
    sa, sb = summary(fa), summary(fb)
    print(f"== {title}")
    print(f"{'metric':<20}{'A':>12}{'B':>12}{'delta':>10}")
    for k in sa:
        d = "" if k == "frames" or not sa[k] else f"{(sb[k] - sa[k]) / sa[k] * 100:+.1f}%"
        print(f"{k:<20}{sa[k]:>12.3f}{sb[k]:>12.3f}{d:>10}")
    print()
print(f"A = {pa}\nB = {pb}")
print("Note: the two runs must use the same window size; check 'dirty cells/frame' is close.")
