"""
Behaviour matrix for scripts/ci/mux-release-guard.sh, which keeps a release run from replacing a
published ntilde-mux (Codex review of PR #511, P1).

Every installed App pins the SHA-256 of its own version's ntilde-mux-<rid> (Phase 5 Task 10). A rerun
for a tag that already has them signs the macOS binary again, with a new secure timestamp, so it gets
new bytes and a new hash; uploading that replaces the asset every installed App of that version
verifies against, and remote installs on macOS hosts break. So:

  * `preflight <tag>` runs in release_metadata, before anything is built, signed or uploaded, and fails
    when the tag's release already carries ANY ntilde-mux asset. A release that does not exist yet
    passes; a gh failure it cannot read as "not found" fails closed.
  * `uploaded <rid>` runs after the publish_mux_daemon leg's upload (`overwrite_files: false`, which the
    pinned action treats as SKIP, not fail) and fails unless both of the leg's assets were really
    uploaded by this step, so a skipped upload can never be followed by a checksum the App would embed
    for bytes the release does not carry.

`gh` is a fake on PATH that records its arguments and answers as each case says; jq is the real one.
Plain python with no test framework, matching the rest of scripts/tests. Run it directly:

    python scripts/tests/mux_release_guard_tests.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "ci" / "mux-release-guard.sh"

FAKE_GH = """#!/usr/bin/env bash
printf '%s\\n' "$@" > "$FAKE_GH_ARGS"
case "$FAKE_GH_MODE" in
  missing) echo "release not found" >&2; exit 1 ;;
  error)   echo "HTTP 502: Bad Gateway (https://api.github.com/repos/o/r/releases/tags/x)" >&2; exit 1 ;;
  output)  cat "$FAKE_GH_OUTPUT" ;;
  *)       echo "fake gh: unknown mode '$FAKE_GH_MODE'" >&2; exit 99 ;;
esac
"""

APP_ASSETS = [
    "NtildeApp-0.12.0-full.nupkg",
    "ntilde-linux-x64-v0.12.0.AppImage",
    "ntilde-Setup-osx-arm64-v0.12.0.pkg",
    "releases.osx.json",
]


def find_bash():
    for candidate in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
        if Path(candidate).exists():
            return candidate
    return shutil.which("bash")


def release_json(names):
    return json.dumps({"assets": [{"name": n, "size": 1, "state": "uploaded"} for n in names]})


def uploaded_json(names):
    # The action's `assets` output: the uploaded assets' REST objects, uploader removed.
    return json.dumps([{"id": i + 1, "name": n, "label": "", "state": "uploaded"} for i, n in enumerate(names)])


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

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        fake_dir = tmp / "bin"
        fake_dir.mkdir()
        (fake_dir / "gh").write_bytes(FAKE_GH.encode("utf-8"))
        os.chmod(fake_dir / "gh", 0o755)
        args_file = tmp / "gh-args"
        output_file = tmp / "gh-output"

        def run(*args, mode=None, gh_output=None, uploaded=None, cwd=None):
            env = dict(os.environ)
            env["PATH"] = str(fake_dir) + os.pathsep + env.get("PATH", "")
            env["FAKE_GH_ARGS"] = str(args_file)
            env["FAKE_GH_OUTPUT"] = str(output_file)
            env["FAKE_GH_MODE"] = mode or "unset"
            env.pop("UPLOADED_ASSETS", None)
            if uploaded is not None:
                env["UPLOADED_ASSETS"] = uploaded
            if args_file.exists():
                args_file.unlink()
            output_file.write_bytes((gh_output or "").encode("utf-8"))
            return subprocess.run([bash, str(SCRIPT), *args], capture_output=True, text=True, env=env, cwd=cwd)

        def gh_args():
            return args_file.read_text(encoding="utf-8").splitlines() if args_file.exists() else None

        def detail(proc):
            return f"exit {proc.returncode} stdout={proc.stdout.strip()!r} stderr={proc.stderr.strip()!r} gh={gh_args()!r}"

        # ---- preflight <tag> --------------------------------------------------------------------

        def passes(name, proc):
            check(name, proc.returncode == 0 and gh_args() == ["release", "view", "v0.12.0", "--json", "assets"], detail(proc))

        def refused(name, proc, *must_mention):
            text = proc.stderr
            ok = (proc.returncode != 0
                  and all(m in text for m in must_mention)
                  and "installed apps pin their hashes" in text
                  and "Publish a new version instead of rerunning" in text)
            check(name, ok, detail(proc))

        passes("preflight: no release for the tag yet", run("preflight", "v0.12.0", mode="missing", cwd=tempfile.gettempdir()))
        passes("preflight: release with no assets", run("preflight", "v0.12.0", mode="output", gh_output=release_json([])))
        passes("preflight: release with only App assets", run("preflight", "v0.12.0", mode="output", gh_output=release_json(APP_ASSETS)))

        refused("preflight: the signed macOS binary is there",
                run("preflight", "v0.12.0", mode="output", gh_output=release_json(APP_ASSETS + ["ntilde-mux-osx-arm64"])),
                "v0.12.0", "ntilde-mux-osx-arm64")
        refused("preflight: only a checksum is there",
                run("preflight", "v0.12.0", mode="output", gh_output=release_json(["ntilde-mux-linux-x64.sha256"])),
                "ntilde-mux-linux-x64.sha256")
        refused("preflight: all six from a finished run",
                run("preflight", "v0.12.0", mode="output", gh_output=release_json(
                    [f"ntilde-mux-{rid}{ext}" for rid in ("linux-x64", "linux-arm64", "osx-arm64") for ext in ("", ".sha256")])),
                "ntilde-mux-linux-arm64", "ntilde-mux-osx-arm64.sha256")
        refused("preflight: CRLF output (a native Windows gh)",
                run("preflight", "v0.12.0", mode="output", gh_output=release_json(["ntilde-mux-osx-arm64"]).replace("\n", "\r\n") + "\r\n"),
                "ntilde-mux-osx-arm64")

        # Anything that is not a readable answer fails closed: the guard never passes on a guess.
        proc = run("preflight", "v0.12.0", mode="error")
        check("preflight: another gh failure fails closed", proc.returncode != 0 and "HTTP 502" in proc.stderr, detail(proc))
        proc = run("preflight", "v0.12.0", mode="output", gh_output="<html>rate limited</html>")
        check("preflight: output that is not JSON fails closed", proc.returncode != 0 and proc.stderr.strip() != "", detail(proc))
        proc = run("preflight", "v0.12.0", mode="output", gh_output=json.dumps({"name": "v0.12.0"}))
        check("preflight: JSON without an assets list fails closed", proc.returncode != 0 and proc.stderr.strip() != "", detail(proc))
        proc = run("preflight", "v0.12.0", mode="output", gh_output="")
        check("preflight: empty output fails closed", proc.returncode != 0 and proc.stderr.strip() != "", detail(proc))
        proc = run("preflight", mode="missing")
        check("preflight: no tag is a usage error", proc.returncode != 0 and gh_args() is None, detail(proc))
        proc = run("preflight", "", mode="missing")
        check("preflight: an empty tag is a usage error", proc.returncode != 0 and gh_args() is None, detail(proc))

        # The tag reaches gh as one argument and is never evaluated (workflow_dispatch input).
        marker = tmp / "pwned"
        hostile = f'v1"; touch "{marker.as_posix()}"; #'
        proc = run("preflight", hostile, mode="missing")
        check("preflight: a hostile tag is data", proc.returncode == 0 and not marker.exists()
              and gh_args() == ["release", "view", hostile, "--json", "assets"], detail(proc))

        # ---- uploaded <rid> ---------------------------------------------------------------------

        both = ["ntilde-mux-osx-arm64", "ntilde-mux-osx-arm64.sha256"]
        proc = run("uploaded", "osx-arm64", uploaded=uploaded_json(both))
        check("uploaded: both of this leg's assets went up", proc.returncode == 0 and gh_args() is None, detail(proc))
        proc = run("uploaded", "osx-arm64", uploaded=uploaded_json(list(reversed(both))).replace("\n", "\r\n"))
        check("uploaded: order and CRLF do not matter", proc.returncode == 0, detail(proc))

        def skipped(name, proc, *must_mention):
            ok = (proc.returncode != 0
                  and all(m in proc.stderr for m in must_mention)
                  and "Publish a new version instead of rerunning" in proc.stderr)
            check(name, ok, detail(proc))

        skipped("uploaded: both skipped (the action's [] output)",
                run("uploaded", "osx-arm64", uploaded="[]"), "ntilde-mux-osx-arm64", "ntilde-mux-osx-arm64.sha256")
        skipped("uploaded: the binary skipped, the checksum replaced",
                run("uploaded", "osx-arm64", uploaded=uploaded_json(["ntilde-mux-osx-arm64.sha256"])), "ntilde-mux-osx-arm64")
        skipped("uploaded: the checksum skipped",
                run("uploaded", "osx-arm64", uploaded=uploaded_json(["ntilde-mux-osx-arm64"])), "ntilde-mux-osx-arm64.sha256")
        skipped("uploaded: another leg's names do not count",
                run("uploaded", "osx-arm64", uploaded=uploaded_json(["ntilde-mux-linux-x64", "ntilde-mux-linux-x64.sha256"])),
                "ntilde-mux-osx-arm64")
        skipped("uploaded: a longer name is not the asset",
                run("uploaded", "osx-arm64", uploaded=uploaded_json(["ntilde-mux-osx-arm64.old", "ntilde-mux-osx-arm64.sha256"])),
                "ntilde-mux-osx-arm64")

        proc = run("uploaded", "osx-arm64")
        check("uploaded: no output at all fails closed", proc.returncode != 0 and proc.stderr.strip() != "", detail(proc))
        proc = run("uploaded", "osx-arm64", uploaded="not json")
        check("uploaded: output that is not JSON fails closed", proc.returncode != 0 and proc.stderr.strip() != "", detail(proc))
        proc = run("uploaded", "osx-arm64", uploaded=json.dumps({"name": "ntilde-mux-osx-arm64"}))
        check("uploaded: output that is not a list fails closed", proc.returncode != 0 and proc.stderr.strip() != "", detail(proc))
        proc = run("uploaded", uploaded=uploaded_json(both))
        check("uploaded: no rid is a usage error", proc.returncode != 0, detail(proc))

        proc = run("publish", "v0.12.0", mode="missing")
        check("an unknown mode is a usage error", proc.returncode != 0 and gh_args() is None, detail(proc))

    print(f"{passed}/{passed + failures} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
