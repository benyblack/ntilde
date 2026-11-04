"""Own-child/private-PTY fixture for Linux provider facts, never App qualification."""
import fcntl
import json
import os
import select
import signal
import sys
import termios
import time


class OwnedChild:
    """Signal-safe ownership state; an unreaped child PID cannot be reused."""
    def __init__(self):
        self.pid = None
        self.status = None
        self.reaped = False

    def start(self, master, slave, executable, script, run_id):
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGTERM, signal.SIGINT})
        try:
            pid = os.fork()
            if pid == 0:
                signal.pthread_sigmask(signal.SIG_SETMASK, previous)
                exec_owned_child(master, slave, executable, script, run_id)
            self.pid = pid
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)

    def poll(self):
        if self.reaped:
            return self.status
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGTERM, signal.SIGINT})
        try:
            try:
                found, status = os.waitpid(self.pid, os.WNOHANG)
            except ChildProcessError:
                self.reaped = True
                raise
            if found:
                self.status = status
                self.reaped = True
            return self.status
        finally:
            # A pending handler may run here; ownership/reap state is already recorded.
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)

    def wait(self, seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            status = self.poll()
            if status is not None:
                return status
            time.sleep(0.01)
        return None


def receive_private(master, done, seconds):
    """Read at most 1024 bytes under one deadline; callback is fixed fixture code."""
    value = b""
    deadline = time.monotonic() + seconds
    while not done(value) and time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        if select.select([master], [], [], max(0, remaining))[0]:
            chunk = os.read(master, 1024)
            if not chunk or len(chunk) > 1024 - len(value):
                raise RuntimeError("Incomplete or oversized owned-child output")
            value += chunk
    return value


def read_control(seconds):
    if not select.select([sys.stdin], [], [], seconds)[0]:
        raise RuntimeError("Missing bounded fixture control")
    return sys.stdin.readline(16)


def exec_owned_child(master, slave, executable, script, run_id):
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


def control_owned_child(master, child, run_id, name):
    ready = receive_private(master, lambda value: b"\n" in value, 5)
    if ready.strip() != ("READY " + run_id).encode("ascii"):
        raise RuntimeError("Owned child did not become ready")
    print(json.dumps({"parentPid": os.getpid(), "childPid": child.pid, "tty": name}), flush=True)
    command = read_control(10)
    if command == "activity\n":
        os.write(master, b"activity\n")
        expected = ("ACTIVITY " + run_id).encode("ascii")
        activity = receive_private(master, lambda value: expected in value, 3)
        if expected not in activity:
            raise RuntimeError("Owned child did not acknowledge private PTY activity")
        print(json.dumps({"activityComplete": True}), flush=True)
        command = read_control(5)
    if command != "exit\n":
        raise RuntimeError("Invalid fixture shutdown")
    os.write(master, b"exit\n")
    status = child.wait(3)
    if status is None:
        raise RuntimeError("Owned child did not exit")
    return status


def terminate_owned(child):
    """Poll before every signal; after waitpid/ECHILD no numeric PID may be signalled."""
    for sig in (signal.SIGTERM, signal.SIGKILL):
        try:
            if child.poll() is not None or child.reaped:
                return
            try:
                os.kill(child.pid, sig)
            except ProcessLookupError:
                pass
            if child.wait(1) is not None:
                return
        except ChildProcessError:
            return


def run_fixture(executable, script, run_id):
    child = OwnedChild()
    master, slave = os.openpty()
    try:
        name = os.ttyname(slave)
        child.start(master, slave, executable, script, run_id)
        os.close(slave)
        slave = -1
        status = control_owned_child(master, child, run_id, name)
        return 0 if os.WIFEXITED(status) and os.WEXITSTATUS(status) == 0 else 1
    finally:
        if child.pid is not None and not child.reaped:
            terminate_owned(child)
            if child.reaped and child.status is not None:
                print(json.dumps({"cleanupReaped": child.pid}), flush=True)
        os.close(master)
        if slave >= 0:
            os.close(slave)


def stop_fixture(signum, _frame):
    """Convert our parent's SIGTERM into normal unwinding so own-child finally cleanup runs."""
    raise SystemExit(128 + signum)


def main():
    if sys.platform != "linux" or len(sys.argv) != 4:
        raise SystemExit(2)
    executable, script, run_id = sys.argv[1:]
    if len(run_id) != 32 or any(char not in "0123456789abcdef" for char in run_id):
        raise SystemExit(2)
    signal.signal(signal.SIGTERM, stop_fixture)
    return run_fixture(executable, script, run_id)


if __name__ == "__main__":
    raise SystemExit(main())
