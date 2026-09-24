#!/usr/bin/env python3
"""Gate the headless App.Tests lane: the lane's verdict, with an explicit flake allowlist.

The lane's test steps run with continue-on-error so that this script, not `dotnet test`'s exit
code, decides whether the job fails. That is what lets the allowlist excuse a named flake at
all - without it the step would red the job before anything consulted the list. Everything
else fails here:

  * A failing test that is not named in the allowlist fails this step.
  * A hang fails this step, whatever the trx says. The lane used to tolerate it as the #81
    teardown hang, long misfiled as an upstream Avalonia.Headless deadlock
    (AvaloniaUI/Avalonia#21467). It was ours: an off-thread read of Avalonia's
    Dispatcher.UIThread static, landing between two lines of
    HeadlessUnitTestSession.EnsureIsolatedApplication, unwound the one dispatcher loop the
    assembly shares. #416 and #426 retired the readers. The hang then truncated about one
    job-run in three between 2026-09-04 and #416, and none of 332 after #426, so the waiver
    that used to live here is gone. The next hang is a regression, and its dump is uploaded
    with the job's artifacts - that dump is what solved it the last two times.
  * A run that produced no results at all fails this step too. A crash, a discovery failure,
    or a zero-result trx is otherwise indistinguishable from success, and that is exactly
    the "green but broken" shape this gate exists to stop.
  * A truncated run fails, with the count, because the tests it never reached cannot be said
    to have passed. Reporting alone was not enough: a run that executed 1,112 of 3,434 tests
    and lost the rest to xUnit collection aborts reported "no failures" and went green,
    minutes after the same branch had run all 3,434.
  * A test step that exited non-zero fails this step unless the trx names a failing test to
    account for it. continue-on-error hides that exit code from the job, so this script is
    the only thing left to read it; a crashed testhost after its last result looks exactly
    like a clean trx with a failed step.
  * A test step that was skipped or cancelled fails this step: a lane that never ran has not
    passed, and saying so beats the "no results, did not hang" message it would otherwise get.
  * A catastrophic (runner-level) failure in the step log *fails* this step, hang or no hang.
    Those aborts kill whole collections without ever appearing in the trx, which is why the
    summary can read "no failures" precisely because tests were never run. They used to be
    reported and tolerated, and the tolerance was earned: they fired on every run, including
    ones that completed the whole suite cleanly (21208fd: 3,434 executed, 0 failures, 10
    aborts), so blocking would have redded every PR and taught everyone to ignore this lane -
    the dynamic that let the truncation hide in the first place. #411 removed their cause, a
    Timer callback in AgentOutputRegionTracker that let exceptions escape onto a threadpool
    thread, and the count went to zero on both OS lanes of a full run. The tolerance has been
    spent, so it is withdrawn: zero is now the only acceptable number, and the next one to
    appear is a regression rather than weather.

Names are matched as substrings, so an allowlist entry without theory arguments covers
every case of that theory.

Usage: check-app-tests-baseline.py <trx-path> <allowlist-path> <min-executed>
                                   [log-path [step-outcome]]

  min-executed  Floor for the executed count in this lane. Lowering it is a decision, the
                same way growing the flake allowlist is: it means the lane legitimately has
                fewer tests, not that a truncated run should be waved through.
  log-path      Optional. The captured `dotnet test` output for this lane, scanned for
                runner-level aborts that never reach the trx.
  step-outcome  Optional. The test step's `steps.<id>.outcome` - success, failure, cancelled
                or skipped - which is its result *before* continue-on-error rewrites it. An
                empty value, or leaving it out for a local run, skips the exit-code check.
"""

import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def summary(line: str) -> None:
    """Echo to the job summary as well as the log, so partial runs are visible at a glance."""
    print(line)
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        try:
            with open(path, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")
        except OSError:
            pass


def read_allowlist(path: Path) -> list[str]:
    if not path.is_file():
        return []
    entries = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            entries.append(line)
    return entries


def read_results(path: Path) -> tuple[list[str], int]:
    """Returns (failed test names, total executed results)."""
    root = ET.parse(path).getroot()
    failures = []
    executed = 0
    for result in root.iter():
        # Tag comparison is namespace-agnostic on purpose: the trx namespace has moved
        # between vstest versions and a silent zero-failure read is the worst outcome here.
        if not result.tag.endswith("UnitTestResult"):
            continue
        executed += 1
        if result.get("outcome") != "Failed":
            continue
        name = result.get("testName")
        if name:
            failures.append(name)
    return sorted(set(failures)), executed


def catastrophic_failures(log: Path) -> list[str]:
    """
    Runner-level aborts from the step log.

    xUnit reports these as "Catastrophic failure: ..." and they take the whole collection with
    them, so the affected tests never produce a UnitTestResult. Nothing about them is visible
    in the trx - which is why a run can lose two thirds of the suite and still be summarised as
    passing. Read from the log because that is the only place they exist.
    """
    if not log.is_file():
        return []
    found = []
    for raw in log.read_text(encoding="utf-8", errors="replace").splitlines():
        marker = "Catastrophic failure"
        index = raw.find(marker)
        if index >= 0:
            found.append(raw[index:].strip())
    # Deduplicated for display, but the caller is told how many times they fired: ten copies of
    # one line is one finding, and also ten dead collections. Both numbers matter.
    return found


def hang_dumps(trx: Path) -> list[Path]:
    """Hang dumps written by --blame-hang-dump-type: the lane stopped making progress."""
    results_dir = trx.parent
    if not results_dir.is_dir():
        return []
    return sorted(results_dir.rglob("*hangdump*.dmp"))


def report_aborts(aborts: list[str], trx: Path) -> None:
    """
    Announce runner-level aborts as the blocking failure they are.

    Kept separate from the result judgement below, and applied to every exit path including
    the tolerated ones, because an abort is independent evidence: it says a collection died
    unread, which no trx and no hang dump can either confirm or excuse.
    """
    summary(
        f"::error::App.Tests hit {len(aborts)} runner-level abort(s) "
        f"({len(set(aborts))} distinct). Each one kills a whole xUnit collection without "
        f"writing a result, so they are invisible to {trx.name} and the tests they took with "
        f"them cannot be said to have passed. This is blocking as of #411, which fixed the "
        f"cause and left both OS lanes at zero. Find what threw and contain it at its source - "
        f"do not reach for the allowlist, which cannot name a test that never reported."
    )
    for line in sorted(set(aborts)):
        summary(f"  - {line}")


def list_partial_failures(failures: list[str]) -> None:
    """
    Name the failures from the part of a rejected run that did execute. Knowing which tests
    failed before the run was cut short is useful even when the run as a whole is rejected.
    """
    if failures:
        summary(f"Failures recorded before the run was cut short ({len(failures)}):")
        for name in failures:
            summary(f"  - {name}")


def judge(trx: Path, allowlist_path: Path, min_executed: int, outcome: str) -> int:
    """Judge the lane's recorded results. Aborts are the caller's verdict, not this one's."""
    if outcome in ("skipped", "cancelled"):
        summary(
            f"::error::App.Tests did not run: its test step was {outcome}, so an earlier step "
            f"in this job failed or the run was stopped. A lane that never ran has not passed. "
            f"Fix whatever failed before it; this lane has nothing to report on its own."
        )
        return 1

    dumps = hang_dumps(trx)
    results = read_results(trx) if trx.is_file() else None

    # First, because a hang explains every other symptom below - the missing trx, the short
    # count, the failed step - and naming it is what points the reader at the dump.
    if dumps:
        recorded = f"{results[1]} executed test(s)" if results else "no trx"
        summary(
            f"::error::App.Tests hung ({dumps[0].name}) with {recorded}; the tests it never "
            f"reached are neither passed nor failed. This lane no longer tolerates that: the "
            f"#81 teardown hang it used to excuse was an off-thread read of Avalonia's "
            f"Dispatcher.UIThread static unwinding HeadlessUnitTestSession's dispatcher loop, "
            f"and #416 and #426 retired the readers the dumps named. A new hang is a regression. "
            f"The dump is in this job's unit-tests-* artifact; TerminalPane.InitializeCommandAssist "
            f"has the account of the last one."
        )
        if results:
            list_partial_failures(results[0])
        return 1

    if results is None:
        summary(
            f"::error::App.Tests produced no results at {trx} and did not hang - no hang dump "
            f"was written. That is a crash or a discovery failure, and it leaves the whole "
            f"lane unjudged. Read the step log above."
        )
        return 1

    allowlist = read_allowlist(allowlist_path)
    failures, executed = results

    if executed == 0:
        summary(
            f"::error::App.Tests wrote {trx.name} but it records no executed tests. The lane "
            f"ran nothing, so its result means nothing. Read the step log above."
        )
        return 1

    if executed < min_executed:
        summary(
            f"::error::App.Tests executed {executed} test(s), below this lane's floor of "
            f"{min_executed}, and nothing hung. The missing tests did not pass - they never "
            f"ran. If the lane legitimately has fewer tests now, lower the floor in ci.yml "
            f"deliberately; do not let a truncated run report success."
        )
        list_partial_failures(failures)
        return 1

    if not failures:
        if outcome == "failure":
            summary(
                f"::error::App.Tests' test step exited non-zero, but {trx.name} records "
                f"{executed} executed and no failing test, and nothing hung. Something failed "
                f"that the trx cannot see - a testhost that crashed after its last result, or a "
                f"data collector error. Read the step log above; continue-on-error keeps that "
                f"exit code off the job, so this is the only place it is judged."
            )
            return 1
        summary(f"App.Tests: {executed} executed, no failures in {trx.name}.")
        return 0

    unexpected = [f for f in failures if not any(entry in f for entry in allowlist)]
    allowed = [f for f in failures if f not in unexpected]

    summary(f"App.Tests: {executed} executed, {len(failures)} failing in {trx.name}.")
    if allowed:
        print(f"\nAllowed by {allowlist_path} ({len(allowed)}):")
        for name in allowed:
            print(f"  - {name}")

    if not unexpected:
        print("\nAll failures are allowlisted.")
        return 0

    summary(f"NOT allowlisted ({len(unexpected)}):")
    for name in unexpected:
        summary(f"  - {name}")
        print(f"::error::App.Tests regression not in the flake allowlist: {name}")

    print(
        f"\nEither fix these, or - if one is genuinely flaky rather than broken - add it to "
        f"{allowlist_path} with a note saying why and what would make it deterministic. "
        f"Do not widen the list to make a red build green."
    )
    return 1


def main() -> int:
    if len(sys.argv) not in (4, 5, 6):
        print(
            f"usage: {Path(sys.argv[0]).name} <trx-path> <allowlist-path> <min-executed> "
            f"[log-path [step-outcome]]",
            file=sys.stderr,
        )
        return 2

    trx = Path(sys.argv[1])
    allowlist_path = Path(sys.argv[2])
    try:
        min_executed = int(sys.argv[3])
    except ValueError:
        print(f"min-executed must be an integer, got {sys.argv[3]!r}", file=sys.stderr)
        return 2
    log_path = Path(sys.argv[4]) if len(sys.argv) >= 5 else None
    # Leaving the argument out (a local run) skips the exit-code check. Passing it empty does
    # not: that is what `${{ steps.<id>.outcome }}` expands to when the id is misspelt, and
    # reading it as "no outcome given" would quietly switch the check off - the exact
    # silent-pass shape this script exists to stop.
    outcome = sys.argv[5].strip().lower() if len(sys.argv) == 6 else ""
    if len(sys.argv) == 6 and outcome not in ("success", "failure", "cancelled", "skipped"):
        print(
            f"step-outcome must be success, failure, cancelled or skipped; got {outcome!r}. "
            f"An empty value usually means the step id in ci.yml does not match.",
            file=sys.stderr,
        )
        return 2

    aborts = catastrophic_failures(log_path) if log_path else []
    code = judge(trx, allowlist_path, min_executed, outcome)

    # Reported after the result judgement, and overriding it, so the log ends on the reason the
    # step is red. A passing verdict above - including a failed step excused by the allowlist -
    # still loses here: the allowlist names tests, and an abort is a collection that died unread.
    if aborts:
        report_aborts(aborts, trx)
        return 1
    return code


if __name__ == "__main__":
    sys.exit(main())
