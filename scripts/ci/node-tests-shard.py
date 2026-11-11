#!/usr/bin/env python3
"""Which of the terminfo script self-test files a CI shard runs.

The Script self-tests job runs a few hundred `node --test` files (215 on 2026-11-10, 237 a day
later). As one job it took up to 14 minutes on windows and was CI's critical path, so it runs as
several shards per OS, each on its own runner. This file is the single place that says which files
each shard runs.

The cut balances itself. scripts/ci/node-tests-durations.json holds each file's measured seconds;
the files are dealt out longest first, each to the shard with the least time so far (REST starts
with a head start: it also runs the job's quick Python and shell checks). A file the table does
not know yet counts as the table's median and still lands wherever there is room - not, as with
the hand-made claims this replaced (#920), always in REST, which made REST the job's longest shard
again within a day (351 s of node beside 122 and 283 on windows, run 42907696288). The cut is
deterministic: the same file list and table give the same shards on every OS and every run.

A shard prints its files longest first, and the workflow runs them in that order through
scripts/ci/node-tests-run.mjs. Files run a few at a time, so a long file that starts late ends the
shard after everything else: on the first run of this cut, which went through `node --test` (it
sorts the files by name), windows shard b's 257 s file started some three minutes in, and the
shard took 456 s beside 248 and 183.

The table is measured, not hand-tuned. Each shard's step records every file's seconds on that
runner (scripts/ci/node-tests-run.mjs) and uploads them; `--update` folds such files into the
table. Refreshing it is downloading a windows run's timing artifacts and running `--update` on
them (the table's own notes give both commands).

Usage: node-tests-shard.py <shard> <file> [<file> ...]   prints the shard's files, one per line.
       node-tests-shard.py --list                         prints the shard names, one per line.
       node-tests-shard.py --plan <file> [<file> ...]     prints each shard's file count and seconds.
       node-tests-shard.py --update <timing.json> [...]   folds measured seconds into the table.
"""

import json
import sys
from pathlib import Path

REST = "rest"
# Workflow order. REST is last; it also runs the job's quick checks, which the table's
# rest_head_start_seconds accounts for. Adding a shard is a name here and in the workflow's matrix
# (scripts/tests/node_tests_shard_tests.py checks the two agree).
SHARDS = ["a", "b", REST]
PREFIX = "scripts/terminfo/"
DURATIONS = Path(__file__).with_name("node-tests-durations.json")


def names():
    """The shard names, in workflow order."""
    return list(SHARDS)


def load_table(path=DURATIONS):
    """The durations table: {"rest_head_start_seconds": n, "seconds": {file: seconds}, ...}."""
    return json.loads(Path(path).read_text(encoding="utf-8"))


def weights(files, table):
    """Each file's seconds from the table, or the table's median for a file it does not list."""
    seconds = table["seconds"]
    known = sorted(seconds.values())
    default = known[len(known) // 2] if known else 1.0
    return {path: float(seconds.get(path, default)) for path in files}


def plan(files, table=None):
    """Every file's shard, and each shard's expected seconds: longest file first, each to the shard
    with the least time so far (ties to the earlier shard, then by file name, so it is stable)."""
    table = load_table() if table is None else table
    weight = weights(files, table)
    totals = dict.fromkeys(SHARDS, 0.0)
    totals[REST] = float(table.get("rest_head_start_seconds", 0))
    owner = {}
    for path in sorted(files, key=lambda p: (-weight[p], p)):
        name = min(SHARDS, key=lambda s: (totals[s], SHARDS.index(s)))
        owner[path] = name
        totals[name] += weight[path]
    return owner, totals


def select(shard, files, table=None):
    """The files of `shard`, longest first (see the module notes), or a ValueError when the request
    cannot partition `files`."""
    if shard not in SHARDS:
        raise ValueError(f"unknown shard {shard!r}; expected one of {', '.join(SHARDS)}")
    if len(set(files)) != len(files):
        raise ValueError("the file list repeats a file")
    if len(files) < len(SHARDS):
        raise ValueError(f"{len(files)} files cannot fill {len(SHARDS)} shards")
    table = load_table() if table is None else table
    owner, _ = plan(files, table)
    weight = weights(files, table)
    chosen = sorted((path for path in files if owner[path] == shard), key=lambda p: (-weight[p], p))
    if not chosen:
        raise ValueError(f"shard {shard!r} owns no file; `node --test` without files would run every test it can find")
    return chosen


def normalize(path):
    """A reporter's file path (absolute, either slash) as the workflow spells it."""
    path = path.replace("\\", "/")
    index = path.rfind("/" + PREFIX)
    return path[index + 1:] if index >= 0 else path


def update(paths, table_path=DURATIONS):
    """Fold timing files written by scripts/ci/node-tests-run.mjs into the table, rounded to
    tenths of a second and at least 0.1 (a file of quick tests still costs its start). A file
    measured in several inputs keeps the largest value."""
    table = load_table(table_path)
    measured = {}
    for path in paths:
        for name, seconds in json.loads(Path(path).read_text(encoding="utf-8")).items():
            name = normalize(name)
            measured[name] = max(measured.get(name, 0.0), float(seconds))
    table["seconds"].update({name: max(round(seconds, 1), 0.1) for name, seconds in measured.items()})
    table["seconds"] = dict(sorted(table["seconds"].items()))
    Path(table_path).write_text(json.dumps(table, indent=2) + "\n", encoding="utf-8")
    return len(measured)


def main(argv):
    args = argv[1:]
    if args == ["--list"]:
        sys.stdout.buffer.write(("\n".join(SHARDS) + "\n").encode())
        return 0
    if args[:1] == ["--plan"] and len(args) > 1:
        owner, totals = plan(args[1:])
        unknown = [p for p in args[1:] if p not in load_table()["seconds"]]
        for name in SHARDS:
            count = sum(1 for o in owner.values() if o == name)
            print(f"{name}: {count} files, {totals[name]:.0f} s")
        print(f"files without a measurement (counted at the median): {len(unknown)}")
        return 0
    if args[:1] == ["--update"] and len(args) > 1:
        print(f"updated {update(args[1:])} files in {DURATIONS.name}")
        return 0
    if len(args) < 2:
        print(__doc__.strip().splitlines()[-4].strip(), file=sys.stderr)
        return 2
    try:
        # Raw bytes: text-mode stdout on windows would end each line with \r\n, and Git Bash would keep the \r on every file name.
        sys.stdout.buffer.write(("\n".join(select(args[0], args[1:])) + "\n").encode())
    except ValueError as error:
        print(f"node-tests-shard: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
