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
    os.write(1, ("READY " + run_id + "\n").encode("ascii"))
    for _ in range(32):
        command = line(0, 60)
        if command == "exit\n":
            return
        if command == "drift\n":
            os.execv(sys.executable, [sys.executable, __file__, "--worker", run_id,
                                     "--ntilde-terminfo-worker=" + run_id, "--drift"])
        if command == "resist\n":
            resistant = True
            os.write(1, b"RESIST\n")
        elif command == "stats\n":
            os.write(1, (str(terms) + "\n").encode("ascii"))
        else:
            raise RuntimeError("Unknown private worker control")


class StopFixture(Exception):
    pass


def parent(run_id):
    terms = 0
    child = None
    reaped = False
    status = None
    master, slave = os.openpty()

    def terminate(_signal, _frame):
        nonlocal terms
        terms += 1
        raise StopFixture()

    signal.signal(signal.SIGTERM, terminate)
    try:
        tty.setraw(slave, termios.TCSANOW)
        child = os.fork()
        if child == 0:
            try:
                signal.signal(signal.SIGTERM, signal.SIG_DFL)
                os.setsid()
                fcntl.ioctl(slave, termios.TIOCSCTTY, 0)
                for fd in (0, 1, 2):
                    os.dup2(slave, fd)
                os.close(master)
                if slave > 2:
                    os.close(slave)
                os.execv(sys.executable, [sys.executable, __file__, "--worker", run_id, "--ntilde-terminfo-worker=" + run_id])
            finally:
                os._exit(2)
        if line(master) != "READY " + run_id + "\n":
            raise RuntimeError("Private worker ready mismatch")
        reply({"parentPid": os.getpid(), "childPid": child, "tty": os.ttyname(slave)})
        for _ in range(32):
            command = line(0, 30)
            if command == "resist\n":
                os.write(master, b"resist\n")
                if line(master) != "RESIST\n":
                    raise RuntimeError("Private worker resistance not acknowledged")
                reply({"resistant": True})
            elif command == "drift\n":
                os.write(master, b"drift\n")
                if line(master) != "READY " + run_id + "\n":
                    raise RuntimeError("Private worker drift not acknowledged")
                reply({"drifted": True})
            elif command == "alive\n":
                os.write(master, b"stats\n")
                reply({"appTerms": terms, "workerTerms": int(line(master))})
            elif command == "zombie\n":
                os.write(master, b"exit\n")
                deadline = time.monotonic() + 5
                observation = None
                while time.monotonic() < deadline and observation is None:
                    observation = os.waitid(os.P_PID, child, os.WEXITED | os.WNOHANG | os.WNOWAIT)
                    if observation is None:
                        time.sleep(0.005)
                if observation is None or observation.si_pid != child:
                    raise RuntimeError("Private zombie not observed")
                reply({"zombiePid": observation.si_pid, "parentWaitWasNowait": True})
            elif command == "reap\n":
                got, status = os.waitpid(child, 0)
                reaped = got == child
                reply({"childReaped": reaped, "pid": got})
            elif command == "close\n":
                break
            else:
                raise RuntimeError("Unknown private parent control")
    except (StopFixture, EOFError):
        pass
    finally:
        if child is not None and not reaped:
            deadline = time.monotonic() + 3
            got, status = os.waitpid(child, os.WNOHANG)
            if not got:
                os.kill(child, signal.SIGTERM)  # Own unreaped child only, test fixture cleanup.
            while not got and time.monotonic() < deadline:
                got, status = os.waitpid(child, os.WNOHANG)
                if not got:
                    time.sleep(0.005)
            if not got:
                os.kill(child, signal.SIGKILL)
                got, status = os.waitpid(child, 0)
            reaped = got == child
        os.close(master)
        os.close(slave)
    if not reaped:
        raise RuntimeError("Private worker reap unconfirmed")
    reply({"fixtureCleanup": True, "childReaped": reaped, "childPid": child, "appTerms": terms})


def main():
    if sys.platform != "linux":
        raise SystemExit(2)
    if len(sys.argv) == 3 and sys.argv[1] == "--nonparent":
        fd = os.pidfd_open(int(sys.argv[2]), 0)
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
        return
    if len(sys.argv) == 2 and sys.argv[1] == "--sentinel":
        reply({"sentinelReady": True, "pid": os.getpid()})
        for _ in range(8):
            command = line(0, 60)
            if command == "close\n":
                return
            if command != "alive\n":
                raise RuntimeError("Invalid sentinel control")
            reply({"sentinelAlive": True, "pid": os.getpid()})
        raise RuntimeError("Sentinel control bound exhausted")
    if len(sys.argv) in (4, 5) and sys.argv[1] == "--worker":
        run_id, marker = sys.argv[2:4]
        if len(sys.argv) == 5 and sys.argv[4] != "--drift":
            raise SystemExit(2)
        if marker != "--ntilde-terminfo-worker=" + run_id:
            raise SystemExit(2)
        worker(run_id)
    elif len(sys.argv) == 3:
        run_id, marker = sys.argv[1:]
        if marker != "--ntilde-terminfo-owner=" + run_id:
            raise SystemExit(2)
        parent(run_id)
    else:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
