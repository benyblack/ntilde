"""
Guard for the analyzer and code-style gate in .github/workflows/ci.yml.

The `build` job compiles with -p:RunAnalyzers=false because the `analyzers` job runs them beside it
(PR #715: they were ~45% of the windows build step). That trade is only safe while three things
hold, and losing any of them switches the gate off with nothing turning red:

  1. an `analyzers` job exists and builds the solution with analyzers on;
  2. `unit_tests` (the "Unit Tests (<os>)" roll-up branch protection requires) needs it and fails
     unless it succeeded;
  3. `build` skips analyzers only while 1 and 2 hold.

So this checks them against the real workflow, and against copies of it with each one broken.
Plain text parsing, no PyYAML: these self-tests also run on the windows runners.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"


def jobs(text: str) -> dict:
    """Top-level job name -> its block of text, from the `jobs:` mapping (two-space keys)."""
    body = text.split("\njobs:\n", 1)[1]
    found, name, lines = {}, None, []
    for line in body.splitlines():
        m = re.match(r"^  ([A-Za-z_][\w-]*):\s*$", line)
        if m:
            if name:
                found[name] = "\n".join(lines)
            name, lines = m.group(1), []
        elif name:
            lines.append(line)
    if name:
        found[name] = "\n".join(lines)
    return found


def code(block: str) -> str:
    """The block without its comment lines, so prose about a flag does not count as the flag."""
    return "\n".join(l for l in block.splitlines() if not l.lstrip().startswith("#"))


def problems(text: str) -> list[str]:
    found = jobs(text)
    out = []
    build = code(found.get("build", ""))
    if "RunAnalyzers=false" not in build:
        return out  # analyzers run inside `build`: nothing else needs to hold
    analyzers = code(found.get("analyzers", ""))
    if not analyzers:
        out.append("`build` skips analyzers but there is no `analyzers` job")
    else:
        if not re.search(r"dotnet build -c\b", analyzers):
            out.append("the `analyzers` job does not build the solution")
        if "RunAnalyzers=false" in analyzers:
            out.append("the `analyzers` job turns analyzers off")
        # A job-level `if:` (four spaces in); its steps' own `if:`s - the SDK fallback - are fine.
        if re.search(r"^    if:", analyzers, re.M):
            out.append("the `analyzers` job is conditional, so it can be skipped")
    rollup = code(found.get("unit_tests", ""))
    needs = re.search(r"^\s*needs:\s*\[([^\]]*)\]", rollup, re.M)
    if not needs or "analyzers" not in [n.strip() for n in needs.group(1).split(",")]:
        out.append("`unit_tests` does not need the `analyzers` job")
    if "needs.analyzers.result" not in rollup:
        out.append("`unit_tests` never reads the `analyzers` job's result")
    return out


def main() -> int:
    failures = 0

    def check(ok, label):
        nonlocal failures
        print(("ok   " if ok else "FAIL ") + label)
        if not ok:
            failures += 1

    real = WORKFLOW.read_text(encoding="utf-8")
    found = problems(real)
    check(not found, f"ci.yml keeps the analyzer gate ({found})")
    check("RunAnalyzers=false" in code(jobs(real).get("build", "")),
          "the build job skips analyzers (else this guard checks nothing)")

    analyzers_block = jobs(real)["analyzers"]
    mutants = {
        "no analyzers job": real.replace("\n  analyzers:\n", "\n  analyzers_gone:\n"),
        "analyzers job turns them off": real.replace(
            analyzers_block, analyzers_block.replace("--no-restore\n", "--no-restore -p:RunAnalyzers=false\n", 1)),
        "roll-up does not need it": real.replace(
            "needs: [build_and_unit_tests, script_self_tests, analyzers]",
            "needs: [build_and_unit_tests, script_self_tests]"),
        "roll-up ignores its result": real.replace("needs.analyzers.result", "needs.analyzers.outputs"),
        "analyzers job made conditional": real.replace(
            "\n  analyzers:\n    name: Analyzers\n", "\n  analyzers:\n    name: Analyzers\n    if: false\n"),
    }
    for label, text in mutants.items():
        check(text != real, f"mutant applies: {label}")
        check(bool(problems(text)), f"caught: {label}")

    print(f"{'all' if not failures else failures} analyzer gate checks {'passed' if not failures else 'FAILED'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
