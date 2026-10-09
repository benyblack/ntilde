"""
Behaviour matrix for scripts/ci/aot-gate-paths.sh, the path filter behind the aot_gate_detect job.

The filter decides whether mux_daemon_aot runs on a PR, and that job produces the linux-x64
ntilde-mux binary native_ssh_docker_e2e needs. When it said "false" for a PR that touched only
the E2E tests, the remote-persistence step skipped with a notice and the job stayed green, so
the tests never ran. Rows here are scoped by directory on purpose: the guard must not be an
allowlist of file spellings.
"""
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "aot-gate-paths.sh"

CASES = [
    # (paths, expected)
    (["tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxDockerE2eTests.cs"], "true"),
    (["tests/Ntilde.App.Tests/Shell/Mux/AnyOtherNewFile.cs"], "true"),
    (["tests/Ntilde.Mux.Tests/Anything.cs"], "true"),
    (["tests/Ntilde.Platform.Tests/Ssh/DockerSshFixture.cs"], "true"),
    (["tests/Ntilde.ExternalSuites/NativeSsh/run.sh"], "true"),
    (["src/Ntilde.Mux/MuxClient.cs"], "true"),
    (["Directory.Build.props"], "true"),
    (["Directory.Packages.props"], "true"),
    (["global.json"], "true"),
    ([".github/workflows/ci.yml"], "true"),
    (["scripts/mux-daemon-smoke.sh"], "true"),
    (["docs/x.md"], "false"),
    (["tests/Ntilde.VT.Tests/x.cs"], "false"),
    (["tests/Ntilde.App.Tests/Shell/Other/x.cs"], "false"),
    (["tests/Ntilde.Platform.Tests/Other/x.cs"], "false"),
    ([], "false"),
    (["docs/x.md", "tests/Ntilde.Mux.Tests/y.cs"], "true"),
]


def find_bash():
    for candidate in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
        if Path(candidate).exists():
            return candidate
    return shutil.which("bash")


def main():
    bash = find_bash()
    if bash is None:
        print("bash not found")
        return 1
    failures = 0
    for paths, expected in CASES:
        stdin = "".join(p + "\n" for p in paths)
        proc = subprocess.run(
            [bash, str(SCRIPT)], input=stdin, capture_output=True, text=True
        )
        want = f"run={expected}\n"
        ok = proc.returncode == 0 and proc.stdout == want
        print(("ok   " if ok else "FAIL ") + repr(paths) + " -> " + repr(proc.stdout.strip())
              + (f" (exit {proc.returncode}) {proc.stderr.strip()}" if not ok else ""))
        failures += 0 if ok else 1
    print(f"{len(CASES) - failures}/{len(CASES)} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
