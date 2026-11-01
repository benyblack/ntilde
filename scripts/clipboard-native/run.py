"""Real clipboard qualification on disposable CI desktops, never a personal clipboard."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import struct
import subprocess
import sys
import tempfile
import time
import zlib
from frames import frame_binary


def fixture(peer=False):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"\0\x65\x43\x21\xff" if peer else b"\0\x12\x34\x56\xff")) + chunk(b"IEND", b"")
    binary = frame_binary(bytes(reversed(range(256)) if peer else range(256)) * 3 + b"\0\xff")
    return {"text/plain": ("native peer héllo 🧪" if peer else "Ntilde héllo 🧪").encode(), "image/png": png,
            "text/html": ("<i>native peer héllo</i>" if peer else "<b>Ntilde héllo</b>").encode(),
            "application/x-ntilde-qa": binary, "application/x-ntilde-qa-alias": binary}


def wait_file(path, processes, deadline):
    while not path.exists():
        for process in processes:
            if process.poll() is not None:
                raise RuntimeError(f"Owned process exited early: {process.returncode}")
        if time.monotonic() >= deadline:
            raise TimeoutError(f"Clipboard handshake timed out: {path.name}")
        time.sleep(0.02)


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--peer", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    if os.environ.get("CI") != "true":
        parser.error("Requires an isolated CI desktop. Do not set CI=true on a personal desktop.")
    values = fixture()
    peer_values = fixture(peer=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    report = {"schemaVersion": 1, "platform": platform.platform(), "passed": False,
              "hostSha256": hashlib.sha256(args.host.read_bytes()).hexdigest(),
              "productionAppSha256": hashlib.sha256(args.host.with_name("Ntilde.dll").read_bytes()).hexdigest(),
              "sourceRevision": os.environ.get("GITHUB_SHA"),
              "workflowRun": os.environ.get("GITHUB_RUN_ID"),
              "fixtureSha256": {k: hashlib.sha256(v).hexdigest() for k, v in values.items()},
              "peerFixtureSha256": {k: hashlib.sha256(v).hexdigest() for k, v in peer_values.items()},
              "binaryFraming": "uint32 big-endian payload length followed by payload; Win32 allocation padding excluded",
              "pngComparison": "validated container and decoded pixel; native compression/metadata may differ",
              "scope": "Production OSC 5522 service and Avalonia native adapter versus an independent OS clipboard peer; no PTY or manual application UI qualification."}
    with tempfile.TemporaryDirectory(prefix="ntilde-native-clipboard-") as temporary:
        root = Path(temporary)
        (root / "fixture.json").write_text(json.dumps({k: base64.b64encode(v).decode() for k, v in values.items()}))
        (root / "peer-fixture.json").write_text(json.dumps({k: base64.b64encode(v).decode() for k, v in peer_values.items()}))
        peer = [str(args.peer.resolve())]
        if args.peer.suffix == ".py":
            peer.insert(0, sys.executable)
        processes = []
        try:
            with (root / "host.log").open("w+") as host_log, (root / "peer.log").open("w+") as peer_log:
                host = subprocess.Popen(["dotnet", str(args.host.resolve()), "--isolated-desktop", str(root)], stdout=host_log, stderr=subprocess.STDOUT)
                processes.append(host)
                wait_file(root / "host-write.ready", [host], time.monotonic() + 40)
                subprocess.run(peer + ["read", str(root)], check=True, timeout=25, stdout=peer_log, stderr=subprocess.STDOUT)
                writer = subprocess.Popen(peer + ["write", str(root)], stdout=peer_log, stderr=subprocess.STDOUT)
                processes.append(writer)
                wait_file(root / "peer-write.ready", processes, time.monotonic() + 25)
                # Host may finish immediately after publishing its final verification marker.
                host.wait(timeout=35)
                if host.returncode != 0 or not (root / "host-read.verified").exists():
                    raise RuntimeError("Production clipboard host did not verify the native read.")
                report["passed"] = True
        except Exception as error:
            report["failure"] = f"{type(error).__name__}: {error}"
            for filename in ["host.log", "peer.log", "error.txt"]:
                path = root / filename
                if path.exists():
                    # Only generated fixture bytes enter this CI-only sandbox.
                    report[filename] = path.read_text(errors="replace")[-12000:]
            print(json.dumps(report, indent=2), file=sys.stderr)
            raise
        finally:
            for process in reversed(processes):
                stop(process)
            args.report.write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
