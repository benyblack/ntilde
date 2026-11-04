"""Own-child/private-PTY fixture for Linux provider facts, never App qualification."""
import fcntl
import json
import os
import select
import signal
import sys
import termios
import time


def wait_owned(pid, seconds):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        found, status = os.waitpid(pid, os.WNOHANG)
        if found:
            return status
        time.sleep(0.01)
    return None


def main():
    if sys.platform != "linux" or len(sys.argv) != 4:
        return 2
    executable, script, run_id = sys.argv[1:]
    if len(run_id) != 32 or any(char not in "0123456789abcdef" for char in run_id):
        return 2
    master, slave = os.openpty()
    name = os.ttyname(slave)
    child = None
    reaped = False
    try:
        child = os.fork()
        if child == 0:
            try:
                os.close(master)
                os.setsid()
                fcntl.ioctl(slave, termios.TIOCSCTTY, 0)
                for fd in (0, 1, 2):
                    os.dup2(slave, fd)
                if slave > 2:
                    os.close(slave)
                os.execv(executable, [executable, script, "--ntilde-terminfo-worker=" + run_id])
            finally:
                os._exit(2)
        os.close(slave)
        slave = -1
        ready = b""
        deadline = time.monotonic() + 5
        while b"\n" not in ready and time.monotonic() < deadline:
            remaining = deadline - time.monotonic()
            if select.select([master], [], [], max(0, remaining))[0]:
                chunk = os.read(master, 1024)
                if len(chunk) > 1024 - len(ready):
                    raise RuntimeError("Oversized owned-child readiness")
                ready += chunk
        if ready.strip() != ("READY " + run_id).encode("ascii"):
            raise RuntimeError("Owned child did not become ready")
        print(json.dumps({"parentPid": os.getpid(), "childPid": child, "tty": name}), flush=True)
        if not select.select([sys.stdin], [], [], 10)[0]:
            raise RuntimeError("Missing bounded fixture control")
        command = sys.stdin.readline(16)
        if command == "activity\n":
            os.write(master, b"activity\n")
            activity = b""
            deadline = time.monotonic() + 3
            expected = ("ACTIVITY " + run_id).encode("ascii")
            while expected not in activity and time.monotonic() < deadline:
                remaining = deadline - time.monotonic()
                if select.select([master], [], [], max(0, remaining))[0]:
                    chunk = os.read(master, 1024)
                    if len(chunk) > 1024 - len(activity):
                        raise RuntimeError("Oversized owned-child activity")
                    activity += chunk
            if expected not in activity:
                raise RuntimeError("Owned child did not acknowledge private PTY activity")
            print(json.dumps({"activityComplete": True}), flush=True)
            if not select.select([sys.stdin], [], [], 5)[0]:
                raise RuntimeError("Missing bounded fixture shutdown")
            command = sys.stdin.readline(16)
        if command != "exit\n":
            raise RuntimeError("Invalid fixture shutdown")
        os.write(master, b"exit\n")
        status = wait_owned(child, 3)
        if status is None:
            raise RuntimeError("Owned child did not exit")
        reaped = True
        return 0 if os.WIFEXITED(status) and os.WEXITSTATUS(status) == 0 else 1
    finally:
        if child and not reaped:
            # Only the PID returned by our own fork is ever signalled; no enumeration or process-name kill.
            for sig in (signal.SIGTERM, signal.SIGKILL):
                try:
                    os.kill(child, sig)
                except ProcessLookupError:
                    pass
                try:
                    if wait_owned(child, 1) is not None:
                        break
                except ChildProcessError:
                    break
        os.close(master)
        if slave >= 0:
            os.close(slave)


if __name__ == "__main__":
    raise SystemExit(main())
