"""
Behaviour matrix for scripts/check-app-tests-baseline.py.

The gate decides whether the headless App.Tests lane is allowed to report success, and it has
real branching: unlisted failures, an empty or missing trx, a truncated run, runner-level
aborts, a hang, a test step that exited non-zero without saying why, and a test step that
never ran. Getting any of those backwards either reds every PR or hides another
green-but-broken run - the exact failure this gate was extended to stop, where a job executed
1,112 of 3,434 tests and reported "no failures".

A hang dump used to waive the executed-count floor, because the #81 teardown hang truncated
roughly one job-run in three and the lane had to tolerate it. #426 removed the cause and 332
CI jobs after it ran without one, so the waiver is gone: a hang now fails the lane like any
other way of not finishing. The cases below pin that from both sides - a hang with a truncated
trx, with a complete one, and with none - so the tolerance cannot quietly come back.

The allowlist is the one thing that may still turn a failed `dotnet test` into a pass, and
only for failures it names. That is why the lane's test steps keep continue-on-error: without
it the step's own exit code would red the job before this gate could consult the list. The
step-outcome cases check the other half of that bargain - a non-zero exit the trx cannot
account for fails here instead of being swallowed.

Plain python with no test framework, matching the rest of scripts/. Run it directly:

    python scripts/tests/check_app_tests_baseline_tests.py

Exits non-zero if any case behaves differently from the table at the bottom.
"""
import shutil, subprocess, sys, tempfile
from pathlib import Path

GATE = Path("scripts/check-app-tests-baseline.py").resolve()
ALLOW = Path("tests/app-tests-known-flaky.txt").resolve()


def make_trx(path: Path, passed: int, failed_names=()):
    results = "".join(
        f'<UnitTestResult testName="Passing{i}" outcome="Passed" />' for i in range(passed)
    ) + "".join(
        f'<UnitTestResult testName="{n}" outcome="Failed" />' for n in failed_names
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>'
        f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results></TestRun>',
        encoding="utf-8",
    )


def run(case, trx, floor, log=None, dump=False, outcome=None, allow=None):
    root = Path(tempfile.mkdtemp())
    trx_path = root / "lane" / "unit_app.trx"
    if trx is None:
        # No trx at all - a crash, a kill, or a testhost that never got to write one.
        trx_path.parent.mkdir(parents=True, exist_ok=True)
    else:
        make_trx(trx_path, trx["passed"], trx.get("failed", ()))
    if dump:
        (trx_path.parent / "testhost_1_hangdump.dmp").write_bytes(b"\x00")
    allow_path = ALLOW
    if allow is not None:
        # The real allowlist is empty, so a case that needs a listed name brings its own.
        allow_path = root / "allow.txt"
        allow_path.write_text("\n".join(allow) + "\n", encoding="utf-8")
    args = [sys.executable, str(GATE), str(trx_path), str(allow_path), str(floor)]
    if log is not None or outcome is not None:
        log_path = trx_path.parent / "console.log"
        log_path.write_text(log or "", encoding="utf-8")
        args.append(str(log_path))
    if outcome is not None:
        args.append(outcome)
    p = subprocess.run(args, capture_output=True, text=True)
    shutil.rmtree(root, ignore_errors=True)
    return p.returncode, (p.stdout + p.stderr)


ABORT_LOG = (
    "[xUnit.net 00:00:07.46]     [FATAL ERROR] System.InvalidOperationException\n"
    "[xUnit.net 00:00:07.46] Catastrophic failure: System.InvalidOperationException : "
    "The calling thread cannot access this object because a different thread owns it.\n"
    "[xUnit.net 00:00:53.56] Catastrophic failure: System.InvalidOperationException : "
    "The calling thread cannot access this object because a different thread owns it.\n"
)

FLAKY = "Some.Flaky.Test"

cases = [
    # name,                                   trx,                       floor, log,       dump,  outcome,   allow,   expect
    ("healthy full run",                      {"passed": 3434},          3300,  "",        False, None,      None,    0),
    ("truncated, nothing hung",               {"passed": 1112},          3300,  "",        False, None,      None,    1),
    # Hangs block now, whatever the trx says. They used to be waived as the #81 teardown hang;
    # #426 fixed its cause, so the next one is a regression, and the lane has to say so.
    ("truncated and hung, blocks",            {"passed": 1112},          3300,  "",        True,  None,      None,    1),
    ("full run and hung, blocks",             {"passed": 3434},          3300,  "",        True,  None,      None,    1),
    ("no trx, hung, blocks",                  None,                      3300,  "",        True,  None,      None,    1),
    # Aborts block, and nothing waives them. They were tolerated while they fired on every run
    # including complete ones (21208fd ran all 3,434 with 10 of them); #411 fixed the cause and
    # both OS lanes went to zero, so the next one is a regression.
    ("aborts in log, full run, blocks",       {"passed": 3434},          3300,  ABORT_LOG, False, None,      None,    1),
    ("aborts in log and hung, blocks",        {"passed": 3434},          3300,  ABORT_LOG, True,  None,      None,    1),
    ("truncated and aborted, blocks",         {"passed": 1112},          3300,  ABORT_LOG, False, None,      None,    1),
    ("no trx, hung, and aborts logged",       None,                      3300,  ABORT_LOG, True,  None,      None,    1),
    ("unlisted failure, full run",            {"passed": 3400, "failed": ["Totally.New.Test"]}, 3300, "", False, None, None, 1),
    ("no log argument, healthy",              {"passed": 3434},          3300,  None,      False, None,      None,    0),
    ("no log argument, truncated",            {"passed": 10},            3300,  None,      False, None,      None,    1),
    ("platformboot lane, healthy",            {"passed": 46},            40,    "",        False, None,      None,    0),
    # Missing or empty results - the crash-shaped cases a green job used to be
    # indistinguishable from.
    ("no trx, nothing hung",                  None,                      3300,  "",        False, None,      None,    1),
    ("trx with zero results",                 {"passed": 0},             3300,  "",        False, None,      None,    1),
    # The test step's own outcome. continue-on-error hides it from the job, so the gate has to
    # read it: success agrees with a clean trx, failure is excused only by a failing test the
    # allowlist names, and a lane whose step never ran has not passed.
    ("step success, healthy",                 {"passed": 3434},          3300,  "",        False, "success", None,    0),
    ("step failed, trx clean, unexplained",   {"passed": 3434},          3300,  "",        False, "failure", None,    1),
    ("step failed on an allowlisted test",    {"passed": 3433, "failed": [FLAKY]}, 3300, "", False, "failure", [FLAKY], 0),
    ("step failed on an unlisted test",       {"passed": 3433, "failed": ["Totally.New.Test"]}, 3300, "", False, "failure", [FLAKY], 1),
    ("step skipped, lane never ran",          None,                      3300,  "",        False, "skipped", None,    1),
    ("step cancelled",                        None,                      3300,  "",        False, "cancelled", None,  1),
    # An empty outcome is what a misspelt step id in ci.yml expands to. It must be a usage
    # error, not "no outcome given", or a typo would silently switch the exit-code check off.
    ("empty outcome is a usage error",        {"passed": 3434},          3300,  "",        False, "",        None,    2),
    ("unknown outcome is a usage error",      {"passed": 3434},          3300,  "",        False, "passed",  None,    2),
]

failures = 0
for name, trx, floor, log, dump, outcome, allow, expect in cases:
    code, out = run(name, trx, floor, log, dump, outcome, allow)
    ok = code == expect
    failures += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {name:<38} exit={code} (want {expect})")
    if not ok:
        print("      " + out.strip().replace("\n", "\n      ")[:600])

print()
print("all gate cases behaved" if failures == 0 else f"{failures} case(s) misbehaved")
sys.exit(1 if failures else 0)
