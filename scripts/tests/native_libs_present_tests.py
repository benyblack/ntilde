"""
Behaviour matrix for scripts/ci/native-libs-present.sh, which decides whether a CI build may pass
SKIP_RUST_NATIVE_BUILD=1 to MSBuild.

Saying "skip" with a library missing would produce a build with no rusty_pty / rusty_ssh beside it
and fail later as a DllNotFoundException far from the cause, so every row that lacks a file - or
has an empty one, the shape a truncated cache restore leaves - must answer "build it yourself".
"""
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "native-libs-present.sh"

N = "src/Ntilde.App/native"
WIN = [f"{N}/target/release/rusty_pty.dll", f"{N}/rusty_ssh/target/release/rusty_ssh.dll"]
LINUX = [f"{N}/target_linux/release/librusty_pty.so", f"{N}/rusty_ssh/target/release/librusty_ssh.so"]
MAC = [f"{N}/target/release/librusty_pty.dylib", f"{N}/rusty_ssh/target/release/librusty_ssh.dylib"]

CASES = [
    # (os, files present (non-empty), files present but empty, expected stdout)
    ("Windows", WIN, [], "skip=1"),
    ("Linux", LINUX, [], "skip=1"),
    ("macOS", MAC, [], "skip=1"),
    # The pre-fix cache payload: rusty_pty only, rusty_ssh never cached.
    ("Windows", WIN[:1], [], "skip="),
    ("Linux", LINUX[:1], [], "skip="),
    ("macOS", MAC[:1], [], "skip="),
    # Linux loads the staged copy; cargo's own output alone is not enough.
    ("Linux", [f"{N}/target/release/librusty_pty.so", LINUX[1]], [], "skip="),
    # Another OS's libraries do not count.
    ("Windows", LINUX + MAC, [], "skip="),
    # A truncated restore.
    ("Windows", WIN[:1], WIN[1:], "skip="),
    ("Linux", [], [], "skip="),
    ("", WIN + LINUX + MAC, [], "skip="),
    ("Plan9", WIN + LINUX + MAC, [], "skip="),
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
    for os_name, present, empty, expected in CASES:
        with tempfile.TemporaryDirectory() as tmp:
            for rel in present:
                path = Path(tmp) / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"\x7fELF")
            for rel in empty:
                path = Path(tmp) / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"")
            proc = subprocess.run(
                [bash, str(SCRIPT), os_name, tmp], capture_output=True, text=True
            )
        ok = proc.returncode == 0 and proc.stdout == expected + "\n"
        label = f"{os_name or '<none>'} present={len(present)} empty={len(empty)}"
        print(("ok   " if ok else "FAIL ") + label + " -> " + repr(proc.stdout.strip()))
        if not ok:
            failures += 1
            sys.stderr.write(proc.stderr)
    print(f"{len(CASES) - failures}/{len(CASES)} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
