"""Self-test only: tagged parent, its direct private-PTY child, and nonparent wait control."""
import errno
import fcntl
import json
import os
import select
import signal
import sys
import termios
import time
import tty

READY_PREFIX = "READY "
WORKER_MARKER = "--ntilde-terminfo-worker="


def reply(value):
    print(json.dumps(value, separators=(",", ":")), flush=True)


def line(fd, seconds=5):
    deadline = time.monotonic() + seconds
    data = bytearray()
    while len(data) < 1024 and time.monotonic() < deadline:
        if select.select([fd], [], [], max(0, deadline - time.monotonic()))[0]:
            byte = os.read(fd, 1)
            if not byte:
                raise EOFError("Private fixture control closed")
            data.extend(byte)
            if byte == b"\n":
                return data.decode("ascii")
    raise RuntimeError("Private fixture control bound exhausted")


def worker(run_id):
    terms = 0
    resistant = False

    def terminate(_signal, _frame):
        nonlocal terms
        terms += 1
        if not resistant:
            raise SystemExit(0)

    signal.signal(signal.SIGTERM, terminate)
    os.write(1, (READY_PREFIX + run_id + "\n").encode("ascii"))
    for _ in range(32):
        command = line(0, 60)
        if command == "exit\n":
            return
        if command == "drift\n":
            os.execv(sys.executable, [sys.executable, __file__, "--worker", run_id,
                                     WORKER_MARKER + run_id, "--drift"])
        if command == "resist\n":
            resistant = True
            os.write(1, b"RESIST\n")
        elif command == "stats\n":
            os.write(1, (str(terms) + "\n").encode("ascii"))
        else:
            raise RuntimeError("Unknown private worker control")


class StopFixture(Exception):
    pass


class ParentFixture:
    def __init__(self, run_id):
        self.run_id, self.terms, self.child, self.reaped = run_id, 0, None, False
        self.master, self.slave = os.openpty()
        signal.signal(signal.SIGTERM, self.stop)

    def stop(self, _signal, _frame):
        self.terms += 1
        raise StopFixture()

    def exec_worker(self):
        try:
            signal.signal(signal.SIGTERM, signal.SIG_DFL)
            os.setsid()
            fcntl.ioctl(self.slave, termios.TIOCSCTTY, 0)
            for fd in (0, 1, 2):
                os.dup2(self.slave, fd)
            os.close(self.master)
            if self.slave > 2:
                os.close(self.slave)
            os.execv(sys.executable, [sys.executable, __file__, "--worker", self.run_id, WORKER_MARKER + self.run_id])
        finally:
            os._exit(2)

    def start(self):
        tty.setraw(self.slave, termios.TCSANOW)
        self.child = os.fork()
        if self.child == 0:
            self.exec_worker()
        if line(self.master) != READY_PREFIX + self.run_id + "\n":
            raise RuntimeError("Private worker ready mismatch")
        reply({"parentPid": os.getpid(), "childPid": self.child, "tty": os.ttyname(self.slave)})

    def worker_command(self, command, expected):
        os.write(self.master, command.encode("ascii"))
        if line(self.master) != expected:
            raise RuntimeError("Private worker command acknowledgment mismatch")

    def retain_zombie(self):
        os.write(self.master, b"exit\n")
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            observation = os.waitid(os.P_PID, self.child, os.WEXITED | os.WNOHANG | os.WNOWAIT)
            if observation is not None and observation.si_pid == self.child:
                return {"zombiePid": observation.si_pid, "parentWaitWasNowait": True}
            time.sleep(0.005)
        raise RuntimeError("Private zombie not observed")

    def await_reap(self, seconds):
        deadline = time.monotonic() + seconds
        while True:
            got, _status = os.waitpid(self.child, os.WNOHANG)
            if got:
                self.reaped = got == self.child
                return self.reaped
            if time.monotonic() >= deadline:
                return False
            time.sleep(0.005)

    def control(self, command):
        if command == "resist\n":
            self.worker_command(command, "RESIST\n")
            return {"resistant": True}
        if command == "drift\n":
            self.worker_command(command, READY_PREFIX + self.run_id + "\n")
            return {"drifted": True}
        if command == "alive\n":
            os.write(self.master, b"stats\n")
            return {"appTerms": self.terms, "workerTerms": int(line(self.master))}
        if command == "zombie\n":
            return self.retain_zombie()
        if command == "reap\n":
            if not self.await_reap(5):
                raise RuntimeError("Private child reap deadline expired")
            return {"childReaped": self.reaped, "pid": self.child}
        if command == "close\n":
            return None
        raise RuntimeError("Unknown private parent control")

    def cleanup(self):
        try:
            if self.child is not None and not self.reaped:
                if not self.await_reap(0):
                    os.kill(self.child, signal.SIGTERM)  # Own unreaped fixture child only.
                if not self.reaped and not self.await_reap(3):
                    os.kill(self.child, signal.SIGKILL)
                if not self.reaped and not self.await_reap(2):
                    raise RuntimeError("Private worker reap unconfirmed")
        finally:
            os.close(self.master)
            os.close(self.slave)


def parent(run_id):
    fixture = ParentFixture(run_id)
    try:
        fixture.start()
        for _ in range(32):
            result = fixture.control(line(0, 30))
            if result is None:
                break
            reply(result)
    except (StopFixture, EOFError):
        pass
    finally:
        fixture.cleanup()
    if not fixture.reaped:
        raise RuntimeError("Private worker reap unconfirmed")
    reply({"fixtureCleanup": True, "childReaped": True, "childPid": fixture.child, "appTerms": fixture.terms})


def nonparent_wait(pid):
    if not 2 <= pid <= 2147483647:
        raise RuntimeError("Invalid private nonparent control PID")
    fd = os.pidfd_open(pid, 0)
    try:
        try:
            os.waitid(os.P_PIDFD, fd, os.WEXITED | os.WNOHANG)
        except ChildProcessError as error:
            if error.errno != errno.ECHILD:
                raise
            reply({"nonParentECHILD": True, "workerReaped": False})
        else:
            raise RuntimeError("Nonparent wait unexpectedly succeeded")
    finally:
        os.close(fd)


def sentinel():
    reply({"sentinelReady": True, "pid": os.getpid()})
    for _ in range(8):
        command = line(0, 60)
        if command == "close\n":
            return
        if command != "alive\n":
            raise RuntimeError("Invalid sentinel control")
        reply({"sentinelAlive": True, "pid": os.getpid()})
    raise RuntimeError("Sentinel control bound exhausted")


def validate_run_id(run_id, marker, prefix):
    if len(run_id) != 32 or any(char not in "0123456789abcdef" for char in run_id) or marker != prefix + run_id:
        raise RuntimeError("Invalid private run identity")


def worker_entry():
    run_id, marker = sys.argv[2:4]
    validate_run_id(run_id, marker, WORKER_MARKER)
    if len(sys.argv) == 5 and sys.argv[4] != "--drift":
        raise SystemExit(2)
    worker(run_id)


def main():
    if sys.platform != "linux":
        raise SystemExit(2)
    if len(sys.argv) == 3 and sys.argv[1] == "--nonparent":
        nonparent_wait(int(sys.argv[2]))
    elif len(sys.argv) == 2 and sys.argv[1] == "--sentinel":
        sentinel()
    elif len(sys.argv) in (4, 5) and sys.argv[1] == "--worker":
        worker_entry()
    elif len(sys.argv) == 3:
        run_id, marker = sys.argv[1:]
        validate_run_id(run_id, marker, "--ntilde-terminfo-owner=")
        parent(run_id)
    else:
        raise SystemExit(2)

if __name__ == "__main__":
    main()
