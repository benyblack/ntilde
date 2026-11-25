"""Disposable direct PTY child for the Modern X11 mouse fixture."""

import json
import os
from pathlib import Path
import re
import select
import socket
import struct
import sys
import termios
import time
import tty


def stamp():
    return str(time.monotonic_ns())


def as_record(attributes):
    return [*attributes[:6], [value if isinstance(value, int) else int.from_bytes(value, "big") for value in attributes[6]]]


def read_input():
    data = os.read(0, 1024)
    if not data:
        raise EOFError("owned PTY closed")
    return data


def quiet():
    if select.select([0], [], [], 0)[0]:
        raise ValueError("stale bytes before an observation window: " + read_input().hex())


def fenced(write_hex, size):
    quiet()
    started = stamp()
    request = bytes.fromhex(write_hex)
    if os.write(1, request) != len(request):
        raise OSError("short mode write")
    reply = bytearray()
    deadline = time.monotonic() + 2
    while len(reply) < size:
        remaining = deadline - time.monotonic()
        if remaining <= 0 or not select.select([0], [], [], remaining)[0]:
            break
        reply.extend(read_input())
    return {"writeHex": request.hex(), "replyHex": reply.hex(), "started": started, "finished": stamp()}


OSC52_REPLY = re.compile(rb"\x1b\]52;c;[A-Za-z0-9+/=]*(?:\x07|\x1b\\)")


def clipboard_query(write_hex, cpr_hex, retry_deadline):
    """Retain the entire stream, including the cursor-position control."""
    quiet()
    request, cpr = bytes.fromhex(write_hex), bytes.fromhex(cpr_hex)
    started = time.monotonic_ns()
    deadline = started + 1_000_000_000
    if retry_deadline is not None:
        deadline = min(deadline, int(retry_deadline))
    if started >= deadline:
        return {"writeHex": "", "rawHex": "", "started": str(started), "finished": stamp(), "lastReadAt": None}
    if os.write(1, request) != len(request):
        raise OSError("short OSC 52 query write")
    reply = bytearray()
    last_read_at = None
    while (remaining := (deadline - time.monotonic_ns()) / 1e9) > 0:
        if not select.select([0], [], [], remaining)[0]:
            continue
        reply.extend(read_input())
        last_read_at = stamp()
        if len(reply) > 1024:
            raise ValueError("oversized OSC 52 query reply: " + reply.hex())
        if cpr in reply and OSC52_REPLY.search(reply):
            break
    return {"writeHex": request.hex(), "rawHex": reply.hex(), "started": str(started),
            "finished": stamp(), "lastReadAt": last_read_at}


class Reader:
    def __init__(self, original):
        self.original = original
        self.active = None
        self.done = False

    def receive(self):
        data = read_input()
        if self.active is None:
            raise ValueError("bytes outside an armed observation window: " + data.hex())
        size = sum(len(chunk["hex"]) for chunk in self.active["chunks"])
        if size + 2 * len(data) > 2048:
            raise ValueError("oversized raw observation")
        self.active["chunks"].append({"at": stamp(), "hex": data.hex()})

    def drain(self):
        deadline = time.monotonic() + 0.24
        while (remaining := deadline - time.monotonic()) > 0 and select.select([0], [], [], remaining)[0]:
            self.receive()

    def seal(self):
        self.drain()
        capture, self.active = self.active, None
        return {**capture, "sealed": stamp()}

    def checkpoint(self, write_hex):
        if self.active["chunks"]:
            raise ValueError("stale input before mode write: " + repr(self.active["chunks"]))
        quiet()
        request = bytes.fromhex(write_hex)
        started = stamp()
        if os.write(1, request) != len(request):
            raise OSError("short armed mode write")
        self.drain()
        chunks, self.active["chunks"] = self.active["chunks"], []
        return {"key": self.active["key"], "writeHex": request.hex(), "started": started,
                "chunks": chunks, "finished": stamp()}

    def command(self, command):
        kind = command["kind"]
        if kind in ("seal", "checkpoint"):
            if self.active is None or self.active["key"] != command["key"]:
                raise ValueError("stale capture key")
            return self.seal() if kind == "seal" else self.checkpoint(command["writeHex"])
        if self.active is not None:
            raise ValueError("observation window remains armed")
        if kind == "arm":
            quiet()
            self.active = {"key": command["key"], "chunks": []}
            return {"key": command["key"], "armed": stamp()}
        if kind == "modes":
            return fenced(command["writeHex"], command["size"])
        if kind == "clipboard-query":
            return clipboard_query(command["writeHex"], command["cprHex"], command.get("retryDeadline"))
        if kind == "restore":
            quiet()
            termios.tcsetattr(0, termios.TCSANOW, self.original)
            self.done = True
            return {
                "original": as_record(self.original),
                "restored": as_record(termios.tcgetattr(0)),
                "at": stamp(),
            }
        raise ValueError("unknown child request")


def peer_parent(connection, owner_pid):
    peer_pid, uid, _ = struct.unpack("3i", connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
    stat = Path(f"/proc/{peer_pid}/stat").read_text().rsplit(")", 1)[1].split()
    if uid != os.getuid() or int(stat[1]) != owner_pid:
        raise ValueError("wrong worker request peer")


def serve(config, server, reader):
    while not reader.done:
        ready = select.select([0, server], [], [], 20)[0]
        if not ready:
            raise TimeoutError("owned worker command deadline")
        if 0 in ready:
            reader.receive()
        if server in ready:
            with server.accept()[0] as connection:
                peer_parent(connection, config["ownerPid"])
                connection.settimeout(2)
                with connection.makefile("rb") as stream:
                    request = json.loads(stream.readline(8193))
                response = json.dumps(reader.command(request)).encode()
                connection.sendall(response + b"\n")


def main():
    config = json.loads(Path(sys.argv[1]).read_bytes())
    if (sys.argv[2] != "--ntilde-terminfo-worker=" + config["runId"]
            or os.environ.get("TERMINFO_RUN_ID") != config["runId"]
            or os.ttyname(0) != os.ttyname(1)):
        raise ValueError("wrong direct worker fixture")
    original = termios.tcgetattr(0)
    reader = Reader(original)
    try:
        tty.setraw(0)
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
            server.bind(config["socket"])
            os.chmod(config["socket"], 0o600)
            server.listen(1)
            os.write(1, ("\x1b[2J\x1b[H" + config["marker"]).encode("ascii"))
            provisional = config["ready"] + ".tmp"
            with open(provisional, "x", encoding="utf-8") as output:
                json.dump({"pid": os.getpid(), "ppid": os.getppid(), "tty": os.ttyname(0)}, output)
            os.replace(provisional, config["ready"])
            serve(config, server, reader)
    finally:
        if not reader.done:
            termios.tcsetattr(0, termios.TCSANOW, original)


if __name__ == "__main__":
    main()
