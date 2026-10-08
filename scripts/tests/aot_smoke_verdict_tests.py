"""
Behaviour matrix for scripts/ci/aot-smoke-verdict.ps1, the log verdict of the AOT gate's smoke
launch. The key negative: the daemon started but did not serve or spawn, while the GUI fell back
to a local shell - that must fail, not pass on the GUI-only spawn line.
"""
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "aot-smoke-verdict.ps1"

THEME = "[TerminalView] Theme applied: 'Default' (was 'Default'); buffer 80x24."
STARTING = "[Mux] starting the multiplexer daemon"
GUI_SPAWN = "[RustPtySession] Spawned 'cmd.exe' pid=1234"
SERVING = "[MuxDaemon] serving ntilde-mux-x (pid 1)"
DAEMON_SPAWN = "[Info] [RustPtySession] Spawned 'cmd.exe' pid=4321"
SPAWNING_ONLY = "[Info] [RustPtySession] Spawning 'cmd.exe' args='/k' cwd='' at 137x40"

CASES = [
    # (name, debug.log lines, mux.log lines or None for no file, expect pass)
    ("daemon path complete", [STARTING, THEME], [SERVING, DAEMON_SPAWN], True),
    ("daemon path complete, GUI also spawned", [STARTING, THEME, GUI_SPAWN], [SERVING, DAEMON_SPAWN], True),
    ("persistence off: GUI spawn only", [THEME, GUI_SPAWN], None, True),
    ("NEGATIVE daemon started, GUI fell back to a local shell, no mux.log", [STARTING, THEME, GUI_SPAWN], None, False),
    ("NEGATIVE daemon started, serving but no spawn, GUI fell back", [STARTING, THEME, GUI_SPAWN], [SERVING], False),
    ("NEGATIVE daemon started, spawn but never served", [STARTING, THEME], [DAEMON_SPAWN], False),
    ("NEGATIVE daemon started, only Spawning (pty_spawn failed)", [STARTING, THEME], [SERVING, SPAWNING_ONLY], False),
    ("NEGATIVE no daemon, no spawn", [THEME], None, False),
    ("NEGATIVE UI never themed", [STARTING], [SERVING, DAEMON_SPAWN], False),
]


def main():
    pwsh = shutil.which("pwsh")
    if pwsh is None:
        print("pwsh not found")
        return 1
    failures = 0
    with tempfile.TemporaryDirectory() as tmp:
        for i, (name, debug, mux, expect_pass) in enumerate(CASES):
            d = Path(tmp) / str(i)
            d.mkdir()
            (d / "debug.log").write_text("\n".join(debug) + "\n", encoding="utf-8")
            mux_path = d / "mux.log"
            if mux is not None:
                mux_path.write_text("\n".join(mux) + "\n", encoding="utf-8")
            proc = subprocess.run(
                [pwsh, "-NoProfile", "-NonInteractive", "-File", str(SCRIPT),
                 "-DebugLog", str(d / "debug.log"), "-MuxLog", str(mux_path)],
                capture_output=True, text=True,
            )
            ok = (proc.returncode == 0) == expect_pass
            print(("ok   " if ok else "FAIL ") + name + f" (exit {proc.returncode})"
                  + ("" if ok else " " + (proc.stdout + proc.stderr).strip()))
            failures += 0 if ok else 1
    print(f"{len(CASES) - failures}/{len(CASES)} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
