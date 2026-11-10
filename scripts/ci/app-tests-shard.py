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

The first claiming shard wins. A prefix may sit inside a namespace a LATER shard claims - one
slow class (`Ntilde.Tests.Core.MainWindowMuxRemoteTests.`) moved out of a busy namespace
(`Ntilde.Tests.Core.`) - and the later shard's filter then excludes it, so the class still runs
exactly once. The reverse, a prefix inside a namespace an EARLIER shard already claims, could
select nothing, and the self-test refuses it.

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
# Four shards from run 42886531622 (PR #921; windows, seconds of test time in each shard's trx,
# plus about 20 s of host start and discovery per shard): CommandAssist 71, Controls 27, Shell
# 55 (52 of it Shell.Mux), Core 142 - of which the four MainWindowMux*Tests classes are 46 (Remote
# 27.5, Sharing 8.7, Update 7.5, Lifecycle 2.6) - and REST's App.Tests share 47, beside the
# non-headless projects (57 s of step, run side by side) and the PlatformBoot lane (14 s). With
# three shards, assist-shell (153 s) and core (142 s) ended the run. Now assist 98 s, mux 101 s,
# core 95 s, and REST's three steps about 136 s, which leaves REST the longest.
#
# Before that, rebalanced from run 41296868089 (windows test-step seconds; ubuntu is lower
# throughout): Core ~124 s of test time; CommandAssist ~62; Shell (almost all Shell.Mux) ~51;
# REST's App.Tests share ~79 s of step, Controls the largest part of it at ~27, plus the
# non-headless projects ~100 and the PlatformBoot lane ~10, which also run on REST. Core alone
# is one shard. The in-process VtReportCliTests stay with CommandAssist; their three tests
# that `dotnet build` the App and the CLI (~76 s) left the shards altogether for the build job
# (VtReportCliBuildTests, Category=SdkBuild), so Shell and Controls join them. Measured on the
# first run of this split (41298327211), windows test steps: assist-shell 149 s, core 132,
# rest 142 (89 non-headless + 45 + 8 PlatformBoot), from 145 / 198 / 189 before.
#
# Floors are the executed counts the measuring run had - identical on windows and ubuntu, since
# the count includes skipped tests - less about 2% for churn. For the four-shard cut, from run
# 42886531622: assist 1,783, mux 1,498, core 1,626 (floors 1,745, 1,465, 1,590). REST's floor is
# unchanged by the cut (it measured 3,584; floor 2,760, set at the three-shard split from 2,817).
# Each shard's timing report (scripts/ci/app-tests-timing.py) prints the count per namespace,
# which is what moving a namespace moves between floors.
SHARDS = [
    ("assist", [
        "Ntilde.Tests.CommandAssist.",
        "Ntilde.Tests.VtReportCliTests.",
        "Ntilde.Tests.Controls.",
    ], 1745),
    ("mux", [
        "Ntilde.Tests.Shell.",
        # Out of Ntilde.Tests.Core., which `core` below claims: see "The first claiming shard
        # wins" above.
        "Ntilde.Tests.Core.MainWindowMuxRemoteTests.",
        "Ntilde.Tests.Core.MainWindowMuxSharingTests.",
        "Ntilde.Tests.Core.MainWindowMuxUpdateTests.",
        "Ntilde.Tests.Core.MainWindowMuxLifecycleTests.",
    ], 1465),
    ("core", ["Ntilde.Tests.Core."], 1590),
    (REST, [], 2760),
]


# What a shard's runner must provide beyond the build artifact, keyed by the output name the
# workflow reads, each with the test-class prefixes that need it. A shard needs an entry when its
# filter selects any class under one of those prefixes. scripts/tests/app_tests_shard_tests.py
# checks the lists against the source, so a new class with the same need cannot be missed.
NEEDS = {
    # Tests that run `dotnet build` on the source tree need `dotnet restore` and the NuGet
    # package cache, about 35 s of setup on windows. None is left in the shards: the only ones,
    # VtReportCliBuildTests, carry Category=SdkBuild and run in the build job, which has a
    # restored and built tree already. A new such test either takes that category or is listed
    # here, and its shard restores; the self-test fails until it does one or the other.
    "restore": [],
    # These spawn real bash, zsh and fish; ubuntu runners have only bash, so the shard installs
    # the other two (without them the zsh and fish tests skip rather than fail).
    "shells": ["Ntilde.Tests.CommandAssist.ShellIntegration.Integration."],
}


def claimed() -> list[str]:
    return [prefix for name, prefixes, _ in SHARDS if name != REST for prefix in prefixes]


def excluded(name: str) -> list[str]:
    """The prefixes earlier shards claimed inside the namespaces shard `name` claims: those tests
    are theirs, so this shard's filter leaves them out (the first claiming shard wins)."""
    earlier: list[str] = []
    for shard, prefixes, _ in SHARDS:
        if shard == name:
            return [q for q in earlier if any(p in q for p in prefixes)]
        earlier += prefixes
    raise KeyError(name)


def filter_for(name: str) -> str:
    for shard, prefixes, _ in SHARDS:
        if shard != name:
            continue
        if shard == REST:
            return "&".join(f"FullyQualifiedName!~{p}" for p in claimed())
        terms = ["(" + "|".join(f"FullyQualifiedName~{p}" for p in prefixes) + ")"]
        terms += [f"FullyQualifiedName!~{q}" for q in excluded(name)]
        return "&".join(terms)
    raise KeyError(name)


def floor_for(name: str) -> int:
    return next(floor for shard, _, floor in SHARDS if shard == name)


def owns(name: str, fqn: str) -> bool:
    """Whether shard `name`'s filter selects `fqn`, with VSTest's `~` (contains) semantics."""
    for shard, prefixes, _ in SHARDS:
        if shard == name:
            if shard == REST:
                return not any(p in fqn for p in claimed())
            return any(p in fqn for p in prefixes) and not any(q in fqn for q in excluded(name))
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
