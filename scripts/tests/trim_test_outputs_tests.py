"""
Checks for scripts/ci/trim-test-outputs.py, which shrinks the build artifact to the native assets
a test runner on the same OS and CPU loads.

Deleting too much is the failure that matters: a test job would then die on a missing native
library (DllNotFoundException in SkiaSharp, rusty_pty, ...) far from the cause. So every row
checks what must survive - the RID folders on the runner's fallback chain, the native libraries
themselves, managed .pdb files, anything outside runtimes/ - as well as what must go.
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "trim-test-outputs.py"

RIDS = ["win", "win-x64", "win-x86", "win-arm64", "linux", "linux-x64", "linux-arm64",
        "linux-musl-x64", "linux-bionic-x64", "unix", "osx", "osx-arm64", "osx-x64",
        "browser-wasm", "win10-x64"]

CASES = [
    # (os, arch, rids that must remain)
    ("Windows", "X64", {"win", "win-x64"}),
    ("Linux", "X64", {"linux", "linux-x64", "linux-musl-x64", "linux-bionic-x64", "unix"}),
    ("Linux", "ARM64", {"linux", "linux-arm64", "unix"}),
    ("macOS", "ARM64", {"osx", "osx-arm64", "unix"}),
]


def build_tree(base: Path) -> Path:
    out = base / "tests" / "Ntilde.App.Tests" / "bin" / "Release" / "net10.0"
    for rid in RIDS:
        native = out / "runtimes" / rid / "native"
        native.mkdir(parents=True)
        (native / "libSkiaSharp.dll").write_bytes(b"x" * 10)
        (native / "libSkiaSharp.pdb").write_bytes(b"p" * 100)
    (out / "runtimes" / "win" / "lib" / "net10.0").mkdir(parents=True)
    (out / "runtimes" / "win" / "lib" / "net10.0" / "System.Thing.dll").write_bytes(b"m")
    (out / "Ntilde.App.Tests.dll").write_bytes(b"a")
    (out / "Ntilde.App.Tests.pdb").write_bytes(b"a")
    (out / "rusty_pty.dll").write_bytes(b"r")
    return out


def main():
    failures = 0

    def check(ok, label):
        nonlocal failures
        print(("ok   " if ok else "FAIL ") + label)
        if not ok:
            failures += 1

    for os_name, arch, expected in CASES:
        with tempfile.TemporaryDirectory() as tmp:
            out = build_tree(Path(tmp))
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), os_name, arch, str(Path(tmp) / "tests")],
                capture_output=True, text=True,
            )
            label = f"{os_name}/{arch}"
            check(proc.returncode == 0, f"{label}: exit 0")
            left = {p.name for p in (out / "runtimes").iterdir()}
            check(left == expected, f"{label}: keeps exactly {sorted(expected)} (left {sorted(left)})")
            natives_ok = all((out / "runtimes" / r / "native" / "libSkiaSharp.dll").is_file() for r in expected)
            check(natives_ok, f"{label}: native libraries in kept folders survive")
            pdbs = [p for p in (out / "runtimes").rglob("*.pdb")]
            check(not pdbs, f"{label}: no native .pdb survives under runtimes/")
            check((out / "Ntilde.App.Tests.pdb").is_file(), f"{label}: managed .pdb outside runtimes/ is kept")
            check((out / "rusty_pty.dll").is_file() and (out / "Ntilde.App.Tests.dll").is_file(),
                  f"{label}: files outside runtimes/ are untouched")
            if "win" in expected:
                check((out / "runtimes/win/lib/net10.0/System.Thing.dll").is_file(),
                      f"{label}: RID-specific managed assets in kept folders survive")
            check("removed" in proc.stdout, f"{label}: reports what it removed")

    with tempfile.TemporaryDirectory() as tmp:
        proc = subprocess.run([sys.executable, str(SCRIPT), "Linux", "X64", str(Path(tmp) / "missing")],
                              capture_output=True, text=True)
        check(proc.returncode == 0, "a missing root is reported, not fatal")

    for bad in ([], ["Linux"], ["Plan9", "X64", "."], ["Linux", "RISCV", "."]):
        proc = subprocess.run([sys.executable, str(SCRIPT), *bad], capture_output=True, text=True)
        check(proc.returncode == 2, f"usage error for {bad or 'no arguments'}")

    print(f"{'all' if not failures else failures} trim checks {'passed' if not failures else 'FAILED'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
