"""
Behaviour matrix for scripts/ci/mux-protocol-range.sh, which release.yml runs to put the build's
multiplexer protocol range into every `vpk pack`'s release notes as
`<!-- ntilde-mux-protocol: <min>-<max> -->` (Phase 5 R10). An installed app reads that marker from
a staged update to decide whether the update keeps its running multiplexer, so a release must
never ship a guessed or partial range: anything the script cannot read for certain fails loudly.

The real MuxProtocol.cs is fed to it, unchanged and with its two literals replaced, so the script
is shown to read the declarations themselves rather than to print a fixed answer.
"""
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "mux-protocol-range.sh"
REAL = ROOT / "src" / "Ntilde.Mux.Contracts" / "MuxProtocol.cs"

MIN_DECL = re.compile(r"(MinSupportedVersion\s*=\s*)[0-9]+(\s*;)")
MAX_DECL = re.compile(r"(MaxSupportedVersion\s*=\s*)[0-9]+(\s*;)")


def find_bash():
    for candidate in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
        if Path(candidate).exists():
            return candidate
    return shutil.which("bash")


def run(bash, *args, cwd=None):
    return subprocess.run([bash, str(SCRIPT), *args], capture_output=True, text=True, cwd=cwd)


def main():
    bash = find_bash()
    if bash is None:
        print("bash not found")
        return 1
    if not SCRIPT.exists():
        print(f"FAIL {SCRIPT} does not exist")
        return 1

    real = REAL.read_text(encoding="utf-8")
    if len(MIN_DECL.findall(real)) != 1 or len(MAX_DECL.findall(real)) != 1:
        print("FAIL the real MuxProtocol.cs no longer declares each constant exactly once; update this test")
        return 1

    def with_values(lo, hi, text=real):
        return MAX_DECL.sub(rf"\g<1>{hi}\g<2>", MIN_DECL.sub(rf"\g<1>{lo}\g<2>", text))

    failures = 0
    passed = 0

    def check(name, ok, detail=""):
        nonlocal failures, passed
        print(("ok   " if ok else "FAIL ") + name + ("" if ok else f" -> {detail}"))
        if ok:
            passed += 1
        else:
            failures += 1

    # The real file, by default path (from any working directory) and given explicitly.
    proc = run(bash, cwd=tempfile.gettempdir())
    check("real file, default path", proc.returncode == 0 and re.fullmatch(r"[1-9][0-9]*-[1-9][0-9]*\n", proc.stdout) is not None,
          f"exit {proc.returncode} stdout={proc.stdout!r} stderr={proc.stderr.strip()!r}")
    if proc.returncode == 0:
        lo, hi = (int(v) for v in proc.stdout.strip().split("-"))
        check("real file, min <= max", lo <= hi, proc.stdout.strip())
    explicit = run(bash, str(REAL))
    check("real file, explicit path", explicit.returncode == 0 and explicit.stdout == proc.stdout,
          f"exit {explicit.returncode} stdout={explicit.stdout!r}")

    with tempfile.TemporaryDirectory() as tmp:
        def case(name, text, expected, newline="\n"):
            path = Path(tmp) / "MuxProtocol.cs"
            path.write_bytes(text.replace("\r\n", "\n").replace("\n", newline).encode("utf-8"))
            proc = run(bash, str(path))
            if expected is None:
                check(name, proc.returncode != 0 and proc.stdout == "" and proc.stderr.strip() != "",
                      f"exit {proc.returncode} stdout={proc.stdout!r} stderr={proc.stderr.strip()!r}")
            else:
                check(name, proc.returncode == 0 and proc.stdout == expected + "\n",
                      f"exit {proc.returncode} stdout={proc.stdout!r} stderr={proc.stderr.strip()!r}")

        case("real file, literals replaced", with_values(7, 9), "7-9")
        case("real file, multi-digit literals", with_values(12, 345), "12-345")
        case("real file, CRLF", with_values(3, 4), "3-4", newline="\r\n")
        case("min missing", MIN_DECL.sub("", real, count=1), None)
        case("max missing", MAX_DECL.sub("", real, count=1), None)
        case("max not a literal", MAX_DECL.sub(r"\g<1>SessionEventsVersion\g<2>", real), None)
        case("min declared twice", real + "\n// MinSupportedVersion = 5;\n", None)
        case("inverted range", with_values(3, 2), None)
        case("zero minimum", with_values(0, 2), None)
        case("empty file", "", None)
        missing = run(bash, str(Path(tmp) / "absent.cs"))
        check("file missing", missing.returncode != 0 and missing.stdout == "",
              f"exit {missing.returncode} stdout={missing.stdout!r}")

    print(f"{passed}/{passed + failures} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
