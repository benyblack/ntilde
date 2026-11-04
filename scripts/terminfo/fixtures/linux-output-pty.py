"""Own-child/private-PTY termios/bytes controls. No App or personal terminal access."""
import fcntl
import importlib.util
import json
import os
import signal
import struct
import sys
import termios
import tty
from pathlib import Path

spec = importlib.util.spec_from_file_location("owned_provider_fixture", Path(__file__).with_name("linux-provider-pty.py"))
provider = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provider)


def snapshot(slave):
    attributes = termios.tcgetattr(slave)
    attributes[6] = [value[0] if isinstance(value, bytes) else value for value in attributes[6]]
    rows, cols, xpixels, ypixels = struct.unpack("HHHH", fcntl.ioctl(slave, termios.TIOCGWINSZ, b"\0" * 8))
    return {"termios": attributes, "rows": rows, "cols": cols, "xpixels": xpixels, "ypixels": ypixels}


def reply(value):
    print(json.dumps(value, separators=(",", ":")), flush=True)


def control(master, slave, child, run_id):
    for _ in range(20):
        command = provider.read_control(5)
        if command == "snapshot\n":
            reply({"snapshot": snapshot(slave)})
        elif command == "probe\n":
            os.write(master, b"probe\n")
            footer = ("END-" + run_id).encode("ascii")
            data = provider.receive_private(master, lambda value, expected=footer: expected in value and value.endswith(b"\n"), 3)
            reply({"hex": data.hex()})
        elif command == "mutate-argv\n":
            os.write(master, b"mutate-argv\n")
            data = provider.receive_private(master, lambda value: value.endswith(b"\n"), 3)
            if data not in (b"MUTATED\n", b"MUTATED\r\n"):
                raise RuntimeError("Private output mutation acknowledgment mismatch")
            reply({"mutated": True})
        elif command == "exit\n":
            os.write(master, b"exit\n")
            status = child.wait(3)
            if status is None or not os.WIFEXITED(status) or os.WEXITSTATUS(status) != 0:
                raise RuntimeError("Private output worker did not exit successfully")
            return {"childReaped": child.pid, "status": status, "final": snapshot(slave)}
        else:
            raise RuntimeError("Invalid bounded output fixture control")
    raise RuntimeError("Private output fixture command bound exhausted")


def run(executable, script, run_id):
    child = provider.OwnedChild()
    master, slave = os.openpty()
    result = None
    try:
        tty.setraw(slave, termios.TCSANOW)
        attributes = termios.tcgetattr(slave)
        attributes[1] |= termios.OPOST | termios.ONLCR
        termios.tcsetattr(slave, termios.TCSANOW, attributes)
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 13, 37, 0, 0))
        child.start(master, slave, executable, script, run_id)
        ready = provider.receive_private(master, lambda value: value.endswith(b"\n"), 5)
        if ready != ("READY " + run_id + "\r\n").encode("ascii"):
            raise RuntimeError("Private output worker readiness mismatch")
        reply({"parentPid": os.getpid(), "childPid": child.pid, "tty": os.ttyname(slave), "initial": snapshot(slave)})
        result = control(master, slave, child, run_id)
    finally:
        if child.pid is not None and not child.reaped:
            provider.terminate_owned(child)
        os.close(master)
        os.close(slave)
    if not child.reaped:
        raise RuntimeError("Private output worker reap unconfirmed")
    reply({"cleanup": result, "childReaped": child.pid, "descriptorsClosed": True})


def main():
    if sys.platform != "linux" or len(sys.argv) != 5:
        raise SystemExit(2)
    executable, script, run_id, marker = sys.argv[1:]
    if len(run_id) != 32 or any(char not in "0123456789abcdef" for char in run_id) or marker != "--ntilde-terminfo-owner=" + run_id:
        raise SystemExit(2)
    signal.signal(signal.SIGTERM, provider.stop_fixture)
    run(executable, script, run_id)


if __name__ == "__main__":
    main()
