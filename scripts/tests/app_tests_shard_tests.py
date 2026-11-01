"""
Checks for scripts/ci/app-tests-shard.py, which splits the headless App.Tests lane across CI shards.

The property that matters is the partition: every test class belongs to exactly one shard. A
class in no shard would silently stop running on every PR - the gate cannot notice, because
each shard's floor only sees its own share - and a class in two would run twice. So this
evaluates each shard's real filter, with VSTest's `~` (contains) semantics, against every class
declared under tests/Ntilde.App.Tests, plus hand-written names for the edge cases.

Also covers the floor output the gate consumes, the per-shard needs (a shard that runs a test
needing `dotnet restore` or real shells must say so, or that test fails or skips on a runner
that was never set up for it), and the usage errors.
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "app-tests-shard.py"
APP_TESTS = ROOT / "tests" / "Ntilde.App.Tests"
# The non-headless projects ci.yml runs on the rest shard, besides its share of App.Tests.
NON_HEADLESS = ["VT", "Rendering", "Architecture", "Platform", "McpServer", "Mux"]

# A test that drives the SDK on the source tree - `dotnet build ...` and the like - rather than
# running an already-built assembly with `dotnet <dll>`. Those need a restored tree.
# The category of the tests CI runs in the build job instead (see VtReportCliBuildTests), and the
# variable that build job sets so they use the build it has just made.
SDK_BUILD_TRAIT = '[Trait("Category", "SdkBuild")]'
USE_EXISTING_BUILD = "NTILDE_SDK_BUILD_TESTS_USE_EXISTING_BUILD"
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
SDK_COMMAND = re.compile(r'\$?"(build|publish|restore|pack|test|run)\s')

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
    "Ntilde.Tests.VtReportCliBuildTests",  # must not ride that class prefix either
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


def declared_classes(root=APP_TESTS, only=None):
    """Namespace-qualified names of the classes under root, from the source. With `only`, just
    the classes in files whose text satisfies it."""
    names = set()
    for path in root.rglob("*.cs"):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        ns = re.search(r"^\s*namespace\s+([\w.]+)", text, re.M)
        if not ns or (only and not only(text)):
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

    # Needs. Each shard prints one line per need; the workflow skips setup on a shard whose
    # line is empty, so the shard owning such a test must print 1.
    needs = {}
    for shard in shards:
        out = dict(line.split("=", 1) for line in run(shard).stdout.splitlines() if "=" in line)
        needs[shard] = {k: v for k, v in out.items() if k not in ("filter", "floor")}
        check(set(needs[shard]) == {"restore", "shells"} and
              all(v in ("", "1") for v in needs[shard].values()),
              f"{shard}: prints restore= and shells= as 1 or empty ({needs[shard]})")

    def owners_of(fqn):
        return [s for s in shards if matches(filters[s], fqn + ".SomeMethod")]

    shells = [c for c in classes
              if c.startswith("Ntilde.Tests.CommandAssist.ShellIntegration.Integration.")]
    check(shells, f"found the real-shell integration classes ({len(shells)})")
    missing = [f"{c} -> {o}" for c in shells for o in owners_of(c) if needs[o].get("shells") != "1"]
    check(not missing, f"every shard running real-shell tests installs the shells ({missing[:3]})")

    def drives_sdk(text):
        return '"dotnet"' in text and SDK_COMMAND.search(text) is not None

    def in_build_job(text):
        return SDK_BUILD_TRAIT in text

    # A test that drives `dotnet build` runs either in CI's build job (Category=SdkBuild, which
    # the shards' lane excludes) or on a shard that restores the tree.
    sdk = declared_classes(only=drives_sdk)
    check("Ntilde.Tests.VtReportCliBuildTests" in sdk,
          f"the detector finds VtReportCliBuildTests' `dotnet build` ({sorted(sdk)})")
    in_build = declared_classes(only=lambda t: drives_sdk(t) and in_build_job(t))
    check("Ntilde.Tests.VtReportCliBuildTests" in in_build,
          "VtReportCliBuildTests carries Category=SdkBuild")
    missing = [f"{c} -> {o}" for c in sorted(set(sdk) - set(in_build)) for o in owners_of(c)
               if needs[o].get("restore") != "1"]
    check(not missing, "every test that drives `dotnet build` takes Category=SdkBuild or runs on a "
                       f"shard that restores (NEEDS['restore'] in app-tests-shard.py): {missing[:3]}")
    others = sorted(c for proj in NON_HEADLESS
                    for c in declared_classes(ROOT / "tests" / f"Ntilde.{proj}.Tests", drives_sdk))
    check(not others or needs["rest"].get("restore") == "1",
          f"no non-headless test drives `dotnet build`, or rest restores the tree ({others[:3]})")

    # ...and the workflow does its half: the shards' lane leaves SdkBuild out, and the build job
    # runs it - with the variable that has the tests use the build that job just made.
    workflow = WORKFLOW.read_text(encoding="utf-8")
    shard_job = workflow.split("\n  build_and_unit_tests:", 1)[-1].split("\n  unit_tests:", 1)[0]
    build_job = workflow.split("\n  build:", 1)[-1].split("\n  build_and_unit_tests:", 1)[0]
    check("Category!=SdkBuild" in shard_job, "the shards' App.Tests lane excludes Category=SdkBuild")
    check('--filter "Category=SdkBuild"' in build_job and USE_EXISTING_BUILD in build_job,
          "the build job runs Category=SdkBuild against its own build")

    for bad in ([], ["nope"], ["shell", "extra"]):
        proc = run(*bad)
        check(proc.returncode == 2, f"usage error for {bad or 'no arguments'}")

    print(f"{'all' if not failures else failures} shard checks {'passed' if not failures else 'FAILED'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
