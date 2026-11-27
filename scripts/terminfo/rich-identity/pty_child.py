"""Direct child for both disposable terminals. Retain the exact PTY read chunks."""

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

DA = re.compile(rb"\x1b\[\?[0-9;]+c")


def exchange(writes):
    chunks = []
    start = time.monotonic_ns()
    stale = os.read(0, 512).hex() if select.select([0], [], [], 0)[0] else None
    sent = []
    for text in writes:
        data = text.encode("ascii")
        count = os.write(1, data)
        if count != len(data):
            raise OSError("short PTY write")
        sent.append(data.hex())
    deadline = time.monotonic() + 2
    while (remaining := deadline - time.monotonic()) > 0 and select.select([0], [], [], remaining)[0]:
        data = os.read(0, 512)
        if not data:
            raise EOFError("PTY closed during exchange")
        chunks.append({"at": str(time.monotonic_ns()), "hex": data.hex()})
        joined = bytes.fromhex("".join(chunk["hex"] for chunk in chunks))
        if len(joined) > 2048:
            raise ValueError("oversized PTY exchange")
        if DA.search(joined):
            deadline = min(deadline, time.monotonic() + 0.18)
    return {"writes": sent, "chunks": chunks, "stale": stale,
            "start": str(start), "end": str(time.monotonic_ns())}


def normalized(attributes):
    return [*attributes[:6], [value if isinstance(value, int) else ord(value) for value in attributes[6]]]


def main(path, marker, owner):
    original = termios.tcgetattr(0)
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
        listener.bind(path)
        listener.listen(1)
        listener.settimeout(45)
        try:
            tty.setraw(0)
            os.write(1, ("\x1b[2J\x1b[2;1H" + marker).encode("ascii"))
            with listener.accept()[0] as peer, peer.makefile("rwb") as stream:
                pid, uid, _ = struct.unpack("3i", peer.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
                if (pid, uid) != (owner, os.getuid()):
                    raise ValueError("unexpected PTY controller credentials")
                peer.settimeout(45)
                for line in stream:
                    writes = json.loads(line)
                    if writes is None:
                        break
                    stream.write(json.dumps(exchange(writes)).encode() + b"\n")
                    stream.flush()
                termios.tcsetattr(0, termios.TCSANOW, original)
                stream.write(json.dumps({"before": normalized(original), "after": normalized(termios.tcgetattr(0))}).encode() + b"\n")
                stream.flush()
        finally:
            termios.tcsetattr(0, termios.TCSANOW, original)
            Path(path).unlink()


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]))
