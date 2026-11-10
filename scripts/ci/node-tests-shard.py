#!/usr/bin/env python3
"""Which of the terminfo script self-test files a CI shard runs.

The Script self-tests job runs about 215 `node --test` files. One job took up to 14 minutes on
windows and was CI's critical path, so the job runs as several shards per OS, each on its own
runner. `node --test --test-shard` cut the list by position, which put the slowest files together
whenever files were added (one shard at 9.7 minutes on windows beside two under 3), so the cut is
made here instead, by measured duration. This file is the single place that says which files each
shard owns.

As in scripts/ci/app-tests-shard.py, every shard but the last claims named files; the last one,
REST, is generated as "none of the above" from the list the workflow passes. So the shards always
partition the list: a new test file lands in REST and still runs, and no edit to the claimed lists
can leave a file unowned (scripts/tests/node_tests_shard_tests.py checks the partition against the
workflow). A claimed file that is no longer in the list is refused, so a rename cannot silently
shrink a shard. REST also runs the job's quick Python and shell checks.

Claims were cut from serial per-file durations measured on macOS (2026-11-10, 215 files, 850 s in
all; windows runs the same files about two to three times slower, and the files that build the
full receipt fixture more than that): each claiming shard holds about 300 s, REST about 255 s of
files plus the quick checks and the receipt-fixture files that weigh most on windows. Rebalancing
means moving a file between the claimed lists, guided by the same measurement
(`node --test <file>` per file, serially).

Usage: node-tests-shard.py <shard> <file> [<file> ...]   prints the shard's files, one per line.
       node-tests-shard.py --list                         prints the shard names, one per line.
"""

import sys

REST = "rest"
PREFIX = "scripts/terminfo/"

# Ordered: (name, claimed files). REST must be last and claims nothing itself.
SHARDS = [
    ("receipts-a", [
        "selective-erase-receipt.test.mjs",       # 102 s
        "query-receipt.test.mjs",                 # 50 s
        "erase-chars-history-receipt.test.mjs",   # 47 s
        "image-publication.test.mjs",             # 31 s
        "rect-edit-contracts.test.mjs",           # 24 s
        "selective-erase-contracts.test.mjs",     # 21 s
        "query-integration.test.mjs",             # 20 s
        "line-edit-receipt.test.mjs",             # 5 s
    ]),
    ("receipts-b", [
        "static-images-receipt.test.mjs",         # 100 s
        "attr-rect-receipt.test.mjs",             # 52 s
        "erase-attrs-region-receipt.test.mjs",    # 41 s
        "saved-reset-receipt.test.mjs",           # 36 s
        "scrollback-receipt.test.mjs",            # 28 s
        "metadata-receipt.test.mjs",              # 20 s
        "rect-edit-receipt.test.mjs",             # 17 s
        "static-images-capture.test.mjs",         # 4 s
    ]),
    (REST, []),
]


def names():
    """The shard names, in workflow order."""
    return [name for name, _ in SHARDS]


def select(shard, files):
    """The files of `shard`, in the order given, or a ValueError when the request cannot partition `files`."""
    if shard not in names():
        raise ValueError(f"unknown shard {shard!r}; expected one of {', '.join(names())}")
    if len(set(files)) != len(files):
        raise ValueError("the file list repeats a file")
    claimed = {}
    for name, claims in SHARDS:
        for claim in claims:
            path = PREFIX + claim
            if path in claimed:
                raise ValueError(f"{path} is claimed by both {claimed[path]} and {name}")
            if path not in files:
                raise ValueError(f"{name} claims {path}, which is not in the file list (renamed or removed?)")
            claimed[path] = name
    chosen = [path for path in files if claimed.get(path, REST) == shard]
    if not chosen:
        raise ValueError(f"shard {shard!r} owns no file; `node --test` without files would run every test it can find")
    return chosen


def main(argv):
    if argv[1:] == ["--list"]:
        print("\n".join(names()))
        return 0
    if len(argv) < 3:
        print(__doc__.strip().splitlines()[-2].strip(), file=sys.stderr)
        return 2
    try:
        print("\n".join(select(argv[1], argv[2:])))
    except ValueError as error:
        print(f"node-tests-shard: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
