#!/usr/bin/env python3
"""Where an App.Tests run spent its time, by namespace and by class, read from its trx files.

Diagnostic only - it never fails a job. It exists so the shard boundaries in
scripts/ci/app-tests-shard.py can be rebalanced from measurements instead of guesses: each
shard prints its own breakdown to the log and the job summary.

Times are the per-test durations the trx records, summed. They leave out what happens between
tests (class fixtures, collection setup, host startup), so a shard's total here is below its
wall-clock time; compare shards with each other, not with the step's duration.

Usage: app-tests-timing.py <trx> [<trx> ...]
"""

import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
TOP = 15


def seconds(duration: str) -> float:
    try:
        h, m, s = duration.split(":")
        return int(h) * 3600 + int(m) * 60 + float(s)
    except ValueError:
        return 0.0


def namespace_of(class_name: str, depth: int) -> str:
    parts = class_name.split("+")[0].split(".")[:-1]
    return ".".join(parts[:depth]) or "(none)"


def read(path: str, per_class: dict, per_ns: dict) -> int:
    root = ET.parse(path).getroot()
    classes = {}
    for test in root.iterfind(".//t:TestDefinitions/t:UnitTest", NS):
        method = test.find("t:TestMethod", NS)
        if method is not None:
            classes[test.get("id")] = method.get("className", "")
    count = 0
    for result in root.iterfind(".//t:Results/t:UnitTestResult", NS):
        cls = classes.get(result.get("testId"), "")
        took = seconds(result.get("duration", "0:0:0"))
        per_class[cls] += took
        per_ns[namespace_of(cls, 4)] += took
        count += 1
    return count


def emit(lines: list[str]) -> None:
    text = "\n".join(lines)
    print(text)
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        try:
            with open(path, "a", encoding="utf-8") as handle:
                handle.write(text + "\n")
        except OSError:
            pass


def table(title: str, totals: dict, limit: int) -> list[str]:
    rows = sorted(totals.items(), key=lambda kv: kv[1], reverse=True)[:limit]
    out = [f"#### {title}", "", "| seconds | |", "|---:|---|"]
    out += [f"| {secs:.1f} | `{name}` |" for name, secs in rows]
    return out + [""]


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: app-tests-timing.py <trx> [<trx> ...]", file=sys.stderr)
        return 2
    per_class: dict = defaultdict(float)
    per_ns: dict = defaultdict(float)
    count = 0
    for path in sys.argv[1:]:
        try:
            count += read(path, per_class, per_ns)
        except (OSError, ET.ParseError) as error:
            print(f"app-tests-timing: skipping {path}: {error}")
    total = sum(per_class.values())
    emit(
        [f"### App.Tests time by namespace ({count} results, {total:.0f} s of test time)", ""]
        + table("Namespaces", per_ns, 40)
        + table(f"Slowest {TOP} classes", per_class, TOP)
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
