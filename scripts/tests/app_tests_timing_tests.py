"""
Checks for scripts/ci/app-tests-timing.py, the per-namespace timing report each App.Tests shard
prints. It is diagnostic only, so the checks are that it reads a real trx shape correctly, that
nested classes count toward their namespace, and that a missing or broken trx never fails the
job.
"""
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "app-tests-timing.py"

TRX = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="a" duration="00:00:02.5000000" outcome="Passed" />
    <UnitTestResult testId="b" duration="00:00:01.0000000" outcome="Passed" />
    <UnitTestResult testId="c" duration="00:01:00.0000000" outcome="Passed" />
  </Results>
  <TestDefinitions>
    <UnitTest id="a"><TestMethod className="Ntilde.Tests.Shell.Mux.FooTests" name="A" /></UnitTest>
    <UnitTest id="b"><TestMethod className="Ntilde.Tests.Shell.Mux.FooTests+Inner" name="B" /></UnitTest>
    <UnitTest id="c"><TestMethod className="Ntilde.Tests.Core.BarTests" name="C" /></UnitTest>
  </TestDefinitions>
</TestRun>
"""


def main():
    failures = 0

    def check(ok, label):
        nonlocal failures
        print(("ok   " if ok else "FAIL ") + label)
        if not ok:
            failures += 1

    with tempfile.TemporaryDirectory() as tmp:
        trx = Path(tmp) / "a.trx"
        trx.write_text(TRX, encoding="utf-8")
        broken = Path(tmp) / "broken.trx"
        broken.write_text("<TestRun", encoding="utf-8")

        proc = subprocess.run([sys.executable, str(SCRIPT), str(trx)], capture_output=True, text=True)
        out = proc.stdout
        check(proc.returncode == 0, "exit 0 on a good trx")
        check("3 results" in out and "64 s" in out, "counts results and sums seconds")
        check("| 60.0 | 1 | `Ntilde.Tests.Core` |" in out, "groups by namespace, with its result count")
        check("| 3.5 | 2 | `Ntilde.Tests.Shell.Mux` |" in out, "a nested class counts toward its namespace")
        check("| 2.5 | 1 | `Ntilde.Tests.Shell.Mux.FooTests` |" in out, "classes carry their own count")
        check(out.index("Ntilde.Tests.Core") < out.index("Ntilde.Tests.Shell.Mux"), "slowest first")

        proc = subprocess.run(
            [sys.executable, str(SCRIPT), str(broken), str(Path(tmp) / "missing.trx"), str(trx)],
            capture_output=True, text=True,
        )
        check(proc.returncode == 0 and "3 results" in proc.stdout,
              "a broken or missing trx is skipped, never fatal")

        bare = Path(tmp) / "bare.trx"
        bare.write_text(TRX.replace(' xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"', ""),
                        encoding="utf-8")
        proc = subprocess.run([sys.executable, str(SCRIPT), str(bare)], capture_output=True, text=True)
        check(proc.returncode == 0 and "3 results" in proc.stdout,
              "elements are matched by local name, with or without the trx namespace")

        proc = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True)
        check(proc.returncode == 2, "no arguments is a usage error")

    print(f"{'all' if not failures else failures} timing checks {'passed' if not failures else 'FAILED'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
