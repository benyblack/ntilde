#!/usr/bin/env python3
"""Read-only environment inventory; never an owned-terminal qualification."""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import sys

TOOLS = ("node", "npm", "git", "dotnet", "cargo")
PLATFORM_BLOCKERS = {
    "darwin": (),
    "linux": (
        "owned-runner-and-worker-reject-non-darwin",
        "linux-process-start-and-pty-identity-not-implemented",
        "linux-output-mode-and-geometry-proof-not-implemented",
        "linux-private-endpoint-and-path-bounds-not-qualified",
        "linux-interruption-and-resistant-worker-cleanup-not-qualified",
    ),
    "win32": (
        "owned-runner-and-worker-reject-non-darwin",
        "windows-process-token-start-and-conpty-identity-not-implemented",
        "windows-private-files-and-owner-pipe-isolation-not-implemented",
        "windows-worker-launch-and-output-mode-proof-not-implemented",
        "windows-interruption-and-resistant-worker-cleanup-not-qualified",
    ),
}
LIVE_GATES = (
    "disposable-desktop-and-display-backend",
    "pinned-input-and-running-binary-provenance",
    "app-worker-and-selected-output-ownership",
    "private-owner-and-observer-endpoints",
    "collection-observation-and-restoration",
    "interruption-resistant-worker-exit-and-complete-cleanup",
)


def _terminal_hint(stream):
    try:
        return bool(stream.isatty())
    except (AttributeError, OSError, ValueError):
        return None


def _directory_hint(path):
    try:
        return Path(path).is_dir()
    except OSError:
        return False


def collect_hints():
    """Inspect only this process's stdio, PATH presence and selected OS metadata."""
    session_type = os.environ.get("XDG_SESSION_TYPE", "").strip().lower()
    if session_type not in ("x11", "wayland", "tty", ""):
        session_type = "other"
    return {
        "toolsOnPath": {name: shutil.which(name) is not None for name in TOOLS},
        "stdio": {
            "stdinIsTty": _terminal_hint(sys.stdin),
            "stdoutIsTty": _terminal_hint(sys.stdout),
        },
        "displayHints": {
            "x11VariablePresent": bool(os.environ.get("DISPLAY")),
            "waylandVariablePresent": bool(os.environ.get("WAYLAND_DISPLAY")),
            "xdgSessionType": session_type or "unset",
            "sshVariablePresent": bool(os.environ.get("SSH_CONNECTION") or os.environ.get("SSH_TTY")),
        },
        "linuxFilesystemHints": {
            "procSelfStatReadable": os.access("/proc/self/stat", os.R_OK),
            "ptsDirectoryPresent": _directory_hint("/dev/pts"),
        } if sys.platform == "linux" else None,
    }


def build_report(platform_name, hints, captured_at):
    """Keep hints separate from fixed policy and unsatisfied live gates."""
    blockers = PLATFORM_BLOCKERS.get(platform_name, ("platform-not-supported-by-owned-adapter",))
    return {
        "schemaVersion": 1,
        "purpose": "environment-inventory-only",
        "capturedAtUtc": captured_at,
        "hostPlatform": platform_name,
        "qualification": "unqualified",
        "liveOwnershipVerified": False,
        "adapterPlatformImplemented": platform_name == "darwin",
        "implementationBlockers": list(blockers),
        "unverifiedLiveGates": list(LIVE_GATES),
        "hints": hints,
        "limits": [
            "Tool presence does not establish executable trust, version or usability.",
            "Display variables and isatty do not prove a graphical desktop or Ntilde ownership.",
            "Filesystem hints do not verify process, PTY, endpoint permissions or identity.",
            "No app, collector, process query, explicit network connection or cleanup is performed.",
            "This JSON is not an ownership proof, measurement report or cleanup receipt.",
        ],
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args(argv)
    timestamp = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    report = build_report(sys.platform, collect_hints(), timestamp)
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
