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

A shard also says what it needs from its runner beyond the compiled test assemblies (NEEDS
below), so a shard that owns no such test skips that setup. Moving a prefix moves its needs with
it: they are worked out from the same filter, never named per shard in the workflow.

Usage: app-tests-shard.py <shard>   prints `filter=`, `floor=` and one `<need>=1` or `<need>=`
                                    line per NEEDS entry, for $GITHUB_OUTPUT.
       app-tests-shard.py --list    prints the shard names, one per line.
The filter is ANDed onto the lane's own Category/Lane exclusions in ci.yml.
"""

import sys

REST = "rest"

# Ordered: (name, namespace prefixes, floor). REST must be last and claims nothing itself.
# A prefix can also name one class ("Ntilde.Tests.VtReportCliTests."), which is how a single
# slow class that lives in a busy namespace gets moved.
#
# Balanced from measured runs on PR #694 (41288590793 and 41289702045), test-step seconds on
# windows / ubuntu: Core ~146 / ~130, VtReportCliTests 68-101 / ~108 (9 tests that drive the
# CLI, the single heaviest class - whichever shard holds it carries it), CommandAssist ~69 / ~58,
# Shell ~51 / ~59, the rest of the lane ~79 / ~59 plus the non-headless projects ~93 / ~80,
# which also run on REST. Pairing VtReportCliTests with Shell left that shard the slowest
# (235 s on windows); with CommandAssist the three shards land near 183 / 197 / 172 s.
#
# Floors are the executed counts those runs measured (identical on windows and ubuntu: the
# count includes skipped tests), less about 2% for churn: assist-cli 1,446, core-shell 2,793,
# rest 3,088 - together 7,327, main's single-lane count at the same commit.
SHARDS = [
    ("assist-cli", ["Ntilde.Tests.CommandAssist.", "Ntilde.Tests.VtReportCliTests."], 1415),
    ("core-shell", ["Ntilde.Tests.Core.", "Ntilde.Tests.Shell."], 2735),
    (REST, [], 3025),
]


# What a shard's runner must provide beyond the build artifact, keyed by the output name the
# workflow reads, each with the test-class prefixes that need it. A shard needs an entry when its
# filter selects any class under one of those prefixes. scripts/tests/app_tests_shard_tests.py
# checks the lists against the source, so a new class with the same need cannot be missed.
NEEDS = {
    # These tests run `dotnet build` of src/Ntilde.App or src/Ntilde.Cli (with --no-restore),
    # so their shard needs `dotnet restore` and the NuGet package cache - about 35 s of setup on
    # windows that no other shard needs, because the others only run the compiled assemblies.
    "restore": ["Ntilde.Tests.VtReportCliTests."],
    # These spawn real bash, zsh and fish; ubuntu runners have only bash, so the shard installs
    # the other two (without them the zsh and fish tests skip rather than fail).
    "shells": ["Ntilde.Tests.CommandAssist.ShellIntegration.Integration."],
}


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


def owns(name: str, fqn: str) -> bool:
    """Whether shard `name`'s filter selects `fqn`, with VSTest's `~` (contains) semantics."""
    for shard, prefixes, _ in SHARDS:
        if shard == name:
            if shard == REST:
                return not any(p in fqn for p in claimed())
            return any(p in fqn for p in prefixes)
    raise KeyError(name)


def needs_for(name: str) -> dict[str, bool]:
    # A probe name inside each prefix. Claimed prefixes end in "." and match by containment, so
    # the shard that selects the probe is the shard that selects the prefix's classes - unless a
    # claimed prefix splits one of these namespaces, which the self-test rejects.
    return {need: any(owns(name, prefix + "AnyClass.AnyMethod") for prefix in prefixes)
            for need, prefixes in NEEDS.items()}


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
        for need, needed in needs_for(arg).items():
            print(f"{need}={'1' if needed else ''}")
    except KeyError:
        names = ", ".join(name for name, _, _ in SHARDS)
        print(f"unknown shard {arg!r}; expected one of: {names}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
