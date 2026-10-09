"""
Behaviour matrix for scripts/ci/check-mux-sha256.sh, which release.yml runs on the ntilde-mux checksums
before it embeds them in the App as pins (Phase 5 Task 10; release hardening item 4). A release build
refuses a remote install for any RID it has no usable pin for, so a checksum file that is missing,
empty, or not exactly `<64 lowercase hex>  ntilde-mux-<rid>` must fail the release here instead of
shipping an App that cannot install ntilde-mux on that platform.
"""
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "check-mux-sha256.sh"
RIDS = ("linux-x64", "linux-arm64", "osx-arm64")
HEX = "0123456789abcdef" * 4


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
    if not SCRIPT.exists():
        print(f"FAIL {SCRIPT} does not exist")
        return 1

    failures = 0
    passed = 0

    def check(name, ok, detail=""):
        nonlocal failures, passed
        print(("ok   " if ok else "FAIL ") + name + ("" if ok else f" -> {detail}"))
        if ok:
            passed += 1
        else:
            failures += 1

    def run(files):
        with tempfile.TemporaryDirectory() as tmp:
            for name, data in files.items():
                (Path(tmp) / name).write_bytes(data)
            return subprocess.run([bash, str(SCRIPT), tmp], capture_output=True, text=True)

    def good(rid):
        return f"{HEX}  ntilde-mux-{rid}\n".encode("ascii")

    def all_good():
        return {f"ntilde-mux-{rid}.sha256": good(rid) for rid in RIDS}

    def detail(proc):
        return f"exit {proc.returncode} stdout={proc.stdout.strip()!r} stderr={proc.stderr.strip()!r}"

    def passes(name, files):
        proc = run(files)
        check(name, proc.returncode == 0, detail(proc))

    def refused(name, files, rid):
        proc = run(files)
        check(name, proc.returncode != 0 and f"ntilde-mux-{rid}.sha256" in proc.stderr, detail(proc))

    passes("all three well formed", all_good())
    passes("an extra file is ignored", {**all_good(), "other.txt": b"x"})
    passes("no trailing newline", {**all_good(), "ntilde-mux-osx-arm64.sha256": f"{HEX}  ntilde-mux-osx-arm64".encode("ascii")})

    for rid in RIDS:
        files = all_good()
        del files[f"ntilde-mux-{rid}.sha256"]
        refused(f"missing: {rid}", files, rid)

    def bad(name, rid, data):
        refused(name, {**all_good(), f"ntilde-mux-{rid}.sha256": data}, rid)

    bad("empty", "linux-arm64", b"")
    bad("uppercase hex", "linux-x64", f"{HEX.upper()}  ntilde-mux-linux-x64\n".encode("ascii"))
    bad("63 hex digits", "linux-x64", f"{HEX[:-1]}  ntilde-mux-linux-x64\n".encode("ascii"))
    bad("65 hex digits", "linux-x64", f"{HEX}0  ntilde-mux-linux-x64\n".encode("ascii"))
    bad("another rid's name", "linux-x64", f"{HEX}  ntilde-mux-linux-arm64\n".encode("ascii"))
    bad("binary-mode marker", "osx-arm64", f"{HEX} *ntilde-mux-osx-arm64\n".encode("ascii"))
    bad("one space", "osx-arm64", f"{HEX} ntilde-mux-osx-arm64\n".encode("ascii"))
    bad("a path in the name", "osx-arm64", f"{HEX}  artifacts/release/ntilde-mux-osx-arm64\n".encode("ascii"))
    bad("CRLF", "linux-x64", f"{HEX}  ntilde-mux-linux-x64\r\n".encode("ascii"))
    bad("two lines", "linux-x64", good("linux-x64") + good("linux-x64"))
    bad("an HTML error page", "linux-x64", b"<html>Not Found</html>\n")

    print(f"\n{passed} passed, {failures} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
