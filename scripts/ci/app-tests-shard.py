#!/usr/bin/env python3
"""Which part of the headless App.Tests main lane a CI shard runs, and its executed-test floor.

The main lane used to run as one `dotnet test` of ~7,200 tests, strictly serial
(parallelizeTestCollections is off), for about six minutes. It was the longest step on CI's
critical path, so the Unit Tests job runs it as several shards, each on its own runner and in
its own process. This file is the single place that says which tests each shard owns.

Shards are cut by namespace. Every shard but the last claims namespace prefixes; the last one,
REST, is generated as "none of the above". So the shards always partition the lane: a new
namespace lands in REST and still runs, and no edit to the claimed lists can leave a test
unowned (scripts/tests/app_tests_shard_tests.py checks the partition). Rebalancing means moving
a prefix between the claiming shards, guided by the per-namespace timing report each shard
prints (scripts/ci/app-tests-timing.py).

Prefixes end in "." so `Ntilde.Tests.Core.` cannot also match `Ntilde.Tests.CoreX`, and they
match through VSTest's `FullyQualifiedName~` (contains), which covers nested namespaces.

Each shard also carries its floor for the gate (scripts/check-app-tests-baseline.py): the
minimum executed count below which a run is treated as truncated rather than green. As with the
single lane's floor before it, lowering one is a decision, not a fix.

Usage: app-tests-shard.py <shard>   prints `filter=...` and `floor=...` for $GITHUB_OUTPUT.
       app-tests-shard.py --list    prints the shard names, one per line.
The filter is ANDed onto the lane's own Category/Lane exclusions in ci.yml.
"""

import sys

REST = "rest"

# Ordered: (name, namespace prefixes, floor). REST must be last and claims nothing itself.
SHARDS = [
    ("shell", ["Ntilde.Tests.Shell."], 500),
    ("assist-core", ["Ntilde.Tests.CommandAssist.", "Ntilde.Tests.Core."], 1500),
    (REST, [], 1500),
]


def claimed() -> list[str]:
    return [prefix for name, prefixes, _ in SHARDS if name != REST for prefix in prefixes]


def filter_for(name: str) -> str:
    for shard, prefixes, _ in SHARDS:
        if shard != name:
            continue
        if shard == REST:
            return "&".join(f"FullyQualifiedName!~{p}" for p in claimed())
        return "(" + "|".join(f"FullyQualifiedName~{p}" for p in prefixes) + ")"
    raise KeyError(name)


def floor_for(name: str) -> int:
    return next(floor for shard, _, floor in SHARDS if shard == name)


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: app-tests-shard.py <shard> | --list", file=sys.stderr)
        return 2
    arg = sys.argv[1]
    if arg == "--list":
        for name, _, _ in SHARDS:
            print(name)
        return 0
    try:
        print(f"filter={filter_for(arg)}")
        print(f"floor={floor_for(arg)}")
    except KeyError:
        names = ", ".join(name for name, _, _ in SHARDS)
        print(f"unknown shard {arg!r}; expected one of: {names}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
