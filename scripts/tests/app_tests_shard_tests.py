"""
Checks for scripts/ci/app-tests-shard.py, which splits the headless App.Tests lane across CI shards.

The property that matters is the partition: every test class belongs to exactly one shard. A
class in no shard would silently stop running on every PR - the gate cannot notice, because
each shard's floor only sees its own share - and a class in two would run twice. So this
evaluates each shard's real filter, with VSTest's `~` (contains) semantics, against every class
declared under tests/Ntilde.App.Tests, plus hand-written names for the edge cases.

Also covers the floor output the gate consumes, and the usage errors.
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "app-tests-shard.py"
APP_TESTS = ROOT / "tests" / "Ntilde.App.Tests"

EDGE_CASES = [
    "Ntilde.Tests.Shell.Mux.Remote.RemoteMuxCommandTests",
    "Ntilde.Tests.Shell.SomeTests",
    "Ntilde.Tests.Core.SettingsWindowTests",
    "Ntilde.Tests.CoreX.LooksLikeCoreTests",  # must not ride the Core prefix
    "Ntilde.Tests.CommandAssist.ShellIntegration.Integration.ZshShellIntegrationTests",
    "Ntilde.Tests.TopLevelTests",  # classes directly in Ntilde.Tests
    "Ntilde.AppTests.AgentHost.SomethingTests",
    "Some.Brand.New.Namespace.Tests",
    "Ntilde.Tests.Shell.Outer+Nested",
    "Ntilde.Tests.VtReportCliTests",  # one class claimed out of the top-level namespace
    "Ntilde.Tests.VtReportCliTestsHelpers",  # must not ride that class prefix
]


def run(*args):
    return subprocess.run(
        [sys.executable, str(SCRIPT), *args], capture_output=True, text=True
    )


def matches(filter_text: str, fqn: str) -> bool:
    """VSTest filter evaluation for the shapes the script emits: an AND of terms, each term a
    single condition or a parenthesised OR of conditions; conditions are `~` / `!~` on
    FullyQualifiedName."""

    def condition(cond: str) -> bool:
        if "!~" in cond:
            prop, value = cond.split("!~", 1)
            assert prop == "FullyQualifiedName", cond
            return value not in fqn
        prop, value = cond.split("~", 1)
        assert prop == "FullyQualifiedName", cond
        return value in fqn

    for term in filter_text.split("&"):
        term = term.strip()
        if term.startswith("(") and term.endswith(")"):
            if not any(condition(c) for c in term[1:-1].split("|")):
                return False
        elif not condition(term):
            return False
    return True


def declared_classes():
    """Namespace-qualified names of the classes in tests/Ntilde.App.Tests, from the source."""
    names = set()
    for path in APP_TESTS.rglob("*.cs"):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        ns = re.search(r"^\s*namespace\s+([\w.]+)", text, re.M)
        if not ns:
            continue
        for cls in re.findall(r"\bclass\s+(\w+)", text):
            names.add(f"{ns.group(1)}.{cls}")
    return sorted(names)


def main():
    failures = 0

    def check(ok, label):
        nonlocal failures
        print(("ok   " if ok else "FAIL ") + label)
        if not ok:
            failures += 1

    listed = run("--list")
    shards = listed.stdout.split()
    check(listed.returncode == 0 and len(shards) >= 2, f"--list names the shards: {shards}")
    check(shards[-1:] == ["rest"], "the complement shard is last")

    filters, floors = {}, {}
    for shard in shards:
        proc = run(shard)
        out = dict(line.split("=", 1) for line in proc.stdout.splitlines() if "=" in line)
        check(proc.returncode == 0 and "filter" in out and "floor" in out,
              f"{shard}: prints filter and floor")
        filters[shard] = out.get("filter", "")
        try:
            floors[shard] = int(out.get("floor", ""))
        except ValueError:
            floors[shard] = -1
        check(floors[shard] > 0, f"{shard}: floor is a positive integer ({out.get('floor')})")
        check(filters[shard] != "", f"{shard}: filter is not empty")
        check('"' not in filters[shard] and " " not in filters[shard],
              f"{shard}: filter needs no quoting inside the workflow's double quotes")

    classes = declared_classes()
    check(len(classes) > 500, f"found the App.Tests classes ({len(classes)})")

    unowned, doubled = [], []
    for fqn in classes + EDGE_CASES:
        owners = [s for s in shards if matches(filters[s], fqn + ".SomeMethod")]
        if not owners:
            unowned.append(fqn)
        elif len(owners) > 1:
            doubled.append(f"{fqn} -> {owners}")
    check(not unowned, f"every class has a shard (unowned: {unowned[:5]})")
    check(not doubled, f"no class has two shards (doubled: {doubled[:5]})")

    owner = {s: matches(filters[s], "Ntilde.Tests.CoreX.LooksLikeCoreTests.M") for s in shards}
    check(owner.get("rest"), "a namespace that merely starts with a claimed one falls to rest")
    owner = {s: matches(filters[s], "Ntilde.Tests.VtReportCliTestsHelpers.M") for s in shards}
    check(owner.get("rest"), "a class that merely starts with a claimed class falls to rest")

    for bad in ([], ["nope"], ["shell", "extra"]):
        proc = run(*bad)
        check(proc.returncode == 2, f"usage error for {bad or 'no arguments'}")

    print(f"{'all' if not failures else failures} shard checks {'passed' if not failures else 'FAILED'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
