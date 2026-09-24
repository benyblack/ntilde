"""Compare headless RenderBenchmark JSONL results between two labels.

Usage: python scripts/render-bench/compare-headless.py <results.jsonl> [labelA labelB]
Labels default to the first two seen in the file, in order.
"""
import json
import statistics as st
import sys
from collections import defaultdict

path = sys.argv[1]
rows = [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]
labels = sys.argv[2:4] if len(sys.argv) >= 4 else list(dict.fromkeys(r["label"] for r in rows))[:2]
la, lb = labels
by = defaultdict(list)
for r in rows:
    by[(r["scenario"], r["label"])].append(r)

for lab in labels:
    r = next(x for x in rows if x["label"] == lab)
    print(f"{lab}: SkiaSharp {r['skia']}, HarfBuzzSharp {r['harfbuzz']}")
print()

hdr = f"{'scenario':<22}{'metric':<9}{la:>10}{lb:>10}{'delta':>9}   {la + ' run range':>19}   {lb + ' run range':>19}"
print(hdr)
print("-" * len(hdr))
for scen in dict.fromkeys(r["scenario"] for r in rows):
    a, b = by[(scen, la)], by[(scen, lb)]
    if not a or not b:
        continue
    for key, name in (("medianMs", "median"), ("p95Ms", "p95"), ("allocKbPerFrame", "alloc KB")):
        va, vb = [x[key] for x in a], [x[key] for x in b]
        ma, mb = st.median(va), st.median(vb)
        d = (mb - ma) / ma * 100 if ma else 0.0
        overlap = "" if key == "allocKbPerFrame" or min(va) <= max(vb) and min(vb) <= max(va) else "  no overlap"
        print(f"{scen:<22}{name:<9}{ma:>10.3f}{mb:>10.3f}{d:>+8.1f}%   {min(va):>9.3f}-{max(va):<9.3f}   {min(vb):>9.3f}-{max(vb):<9.3f}{overlap}")
    print()
print(f"runs per side: {len(by[(rows[0]['scenario'], la)])} / {len(by[(rows[0]['scenario'], lb)])}")
