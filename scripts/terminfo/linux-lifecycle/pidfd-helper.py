"""Pinned, isolated pidfd helper. Exit observation is never a parent reap fact."""
import errno
import json
import os
import select
import signal
import sys
import time

MAX_FRAME = 4096
MAX_COMMANDS = 64
RESERVE_NS = 4_000_000_000
CLOSE_NS = 250_000_000
PHASE_NS = 4_000_000_000


def now():
    return time.clock_gettime_ns(time.CLOCK_MONOTONIC)


def exact(value, names):
    if type(value) is not dict or set(value) != set(names):
        raise RuntimeError("Unexpected lifecycle frame fields")


def integer(value, low, high):
    if type(value) is not int or not low <= value <= high:
        raise RuntimeError("Invalid bounded lifecycle integer")
    return value


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise RuntimeError("Duplicate lifecycle JSON key")
        result[key] = value
    return result


class Transport:
    def __init__(self):
        self.buffer = bytearray()
        self.frames = 0
        self.written = 0
        os.set_blocking(0, False)
        os.set_blocking(1, False)

    def read(self, deadline):
        while True:
            if now() >= deadline:
                raise TimeoutError("Lifecycle input deadline expired")
            frame = self.take_frame()
            if frame is not None:
                return frame
            ready, _, _ = select.select([0], [], [], max(0, (deadline - now()) / 1e9))
            if not ready:
                continue
            self.receive()

    def take_frame(self):
        end = self.buffer.find(b"\n")
        if end < 0:
            return None
        if end + 1 > MAX_FRAME:
            raise RuntimeError("Lifecycle input frame too large")
        raw = bytes(self.buffer[:end])
        del self.buffer[:end + 1]
        self.frames += 1
        if self.frames > MAX_COMMANDS:
            raise RuntimeError("Lifecycle command limit exhausted")
        frame = json.loads(raw.decode("utf-8", "strict"), object_pairs_hook=unique)
        if type(frame) is not dict:
            raise RuntimeError("Expected lifecycle object frame")
        return frame

    def receive(self):
        data = os.read(0, min(1024, MAX_FRAME + 1 - len(self.buffer)))
        if not data:
            if self.buffer:
                raise RuntimeError("Partial lifecycle input frame")
            raise EOFError("Lifecycle input closed")
        self.buffer.extend(data)
        if len(self.buffer) > MAX_FRAME:
            raise RuntimeError("Lifecycle input frame too large")

    def write(self, value, deadline):
        data = (json.dumps(value, separators=(",", ":")) + "\n").encode("ascii")
        if len(data) > MAX_FRAME or len(data) > 262144 - self.written:
            raise RuntimeError("Lifecycle output bound exhausted")
        offset = 0
        while offset < len(data):
            if now() >= deadline:
                raise TimeoutError("Lifecycle output deadline expired")
            _, ready, _ = select.select([], [1], [], max(0, (deadline - now()) / 1e9))
            if ready:
                offset += os.write(1, data[offset:])
        self.written += len(data)


class RoleFailure(RuntimeError):
    def __init__(self, phase, role, cause):
        self.phase, self.role, self.cause = phase, role, cause


class LifecycleFailure(RuntimeError):
    def __init__(self, errors, observation=None):
        self.errors, self.observation = errors, observation


def failure_records(error, phase="run", role=None):
    if isinstance(error, RoleFailure):
        return failure_records(error.cause, error.phase, error.role)
    if isinstance(error, LifecycleFailure):
        return [record for item in error.errors for record in failure_records(item, phase, role)][:8]
    return [{"phase": phase, "role": role, "type": type(error).__name__,
             "errno": error.errno if isinstance(error, OSError) else None,
             "message": str(error).encode("ascii", "backslashreplace")[:128].decode("ascii")}]


class Targets:
    def __init__(self):
        self.fds = {}
        self.exited = {"app": False, "worker": False}
        self.events = {"app": 0, "worker": 0}
        self.attempts = []
        self.closed = False
        self.close_attempted = False

    def open(self, app, worker):
        phase, role = "pidfd-open", None
        try:
            for role, pid in (("app", app), ("worker", worker)):
                self.fds[role] = os.pidfd_open(pid, 0)
            phase, role = "acquisition-poll", None
            self.poll()
            if any(self.exited.values()):
                raise RuntimeError("Original target already exited")
        except Exception as error:
            errors = [RoleFailure(phase, role, error)]
            try:
                self.close()
            except LifecycleFailure as close_error:
                errors.extend(close_error.errors)
            raise LifecycleFailure(errors) from error

    def poll(self):
        if self.closed:
            return
        for role, fd in self.fds.items():
            poller = select.poll()
            poller.register(fd, select.POLLIN)
            events = poller.poll(0)
            bits = events[0][1] if events else 0
            if bits & (select.POLLERR | select.POLLNVAL):
                raise RuntimeError("Invalid pidfd poll result")
            self.events[role] = bits
            if bits & select.POLLIN:
                self.exited[role] = True

    def observation(self):
        self.poll()
        return {"app": {"exitObserved": self.exited["app"], "pollEvents": self.events["app"]},
                "worker": {"exitObserved": self.exited["worker"], "pollEvents": self.events["worker"]},
                "attempts": self.attempts, "pidfdsClosed": self.closed}

    def send(self, role, kind, deadline):
        self.poll()
        if self.exited[role]:
            return
        if now() >= deadline:
            raise TimeoutError("Lifecycle signal deadline expired")
        if len(self.attempts) >= 8:
            raise RuntimeError("Lifecycle signal limit exhausted")
        number = signal.SIGTERM if kind == "TERM" else signal.SIGKILL
        try:
            signal.pidfd_send_signal(self.fds[role], number, None, 0)
            self.attempts.append({"role": role, "signal": kind, "sent": True, "errno": None})
        except OSError as error:
            self.attempts.append({"role": role, "signal": kind, "sent": False, "errno": error.errno})
            if error.errno != errno.ESRCH:
                raise
        self.poll()

    def wait(self, role, deadline):
        while not self.exited[role]:
            if now() >= deadline:
                return
            poller = select.poll()
            poller.register(self.fds[role], select.POLLIN)
            poller.poll(max(1, min(20, (deadline - now()) // 1_000_000)))
            self.poll()

    def terminate(self, role, deadline):
        self.poll()
        if self.exited[role]:
            return
        remaining = deadline - now()
        if remaining <= 0:
            raise TimeoutError("Lifecycle termination deadline expired")
        grace = min(1_000_000_000, remaining // 2)
        self.send(role, "TERM", deadline)
        self.wait(role, min(deadline, now() + grace))
        if not self.exited[role]:
            self.send(role, "KILL", deadline)
            self.wait(role, deadline)
        if not self.exited[role]:
            raise TimeoutError("Original pidfd exit unobserved")

    def abort(self, deadline):
        errors = []
        for role in ("worker", "app"):
            try:
                # Share the original absolute budget; don't renew it per target.
                self.terminate(role, deadline)
            except (OSError, RuntimeError) as error:
                errors.append(RoleFailure("abort", role, error))
        if errors:
            raise LifecycleFailure(errors)

    def close(self):
        if self.close_attempted:
            return  # Never retry a numeric FD whose failed close may already have freed it.
        self.close_attempted = True
        errors = []
        for role, fd in self.fds.items():
            try:
                os.close(fd)
            except OSError as error:
                errors.append(RoleFailure("pidfd-close", role, error))
        self.fds.clear()
        self.closed = not errors
        if errors:
            raise LifecycleFailure(errors)


class Session:
    def __init__(self):
        self.transport, self.targets = Transport(), Targets()
        self.admitted = False
        self.aborted = False
        self.settled = False
        self.hard_deadline = now() + PHASE_NS
        self.last_clock = None
        self.sequence = 0

    def open(self):
        opening = self.transport.read(self.hard_deadline)
        exact(opening, ("op", "seq", "appPid", "workerPid", "lifetimeMs"))
        if opening["op"] != "open" or opening["seq"] != 1:
            raise RuntimeError("Expected initial lifecycle open")
        app = integer(opening["appPid"], 2, 2147483647)
        worker = integer(opening["workerPid"], 2, 2147483647)
        if app == worker:
            raise RuntimeError("Distinct original roles required")
        lifetime = integer(opening["lifetimeMs"], 10000, 300000)
        self.targets.open(app, worker)
        self.hard_deadline = now() + lifetime * 1_000_000
        self.sequence = 1
        self.reply("opened", min(self.hard_deadline, now() + PHASE_NS))

    def reply(self, kind, deadline, clock=None):
        frame = {"seq": self.sequence, "kind": kind, "observation": self.targets.observation()}
        if clock is not None:
            frame["clockNs"] = str(clock)
        self.transport.write(frame, deadline)

    def phase_deadline(self, request, op):
        exact(request, ("seq", "op", "deadlineNs", "lifetimeDeadlineNs") if op == "admit" else ("seq", "op", "deadlineNs"))
        raw = request["deadlineNs"]
        if type(raw) is not str or not raw.isascii() or not raw.isdecimal() or len(raw) > 20:
            raise RuntimeError("Invalid helper absolute deadline")
        deadline = int(raw)
        bound = self.hard_deadline - (CLOSE_NS if op in ("abort", "close") else RESERVE_NS)
        if self.last_clock is None or not now() < deadline <= min(bound, self.last_clock + PHASE_NS):
            raise TimeoutError("Expired/unarmed lifecycle phase")
        self.last_clock = None
        return deadline

    def admit(self, request, deadline):
        if self.admitted or self.aborted:
            raise RuntimeError("Lifecycle admission is one-shot")
        raw = request["lifetimeDeadlineNs"]
        if type(raw) is not str or not raw.isascii() or not raw.isdecimal() or len(raw) > 20:
            raise RuntimeError("Invalid conservative lifecycle lifetime")
        self.hard_deadline = min(self.hard_deadline, int(raw))
        if deadline >= self.hard_deadline - RESERVE_NS:
            raise RuntimeError("Lifecycle lifetime has no cleanup reserve")
        self.targets.poll()
        if any(self.targets.exited.values()):
            raise RuntimeError("Original target exited across admission")
        self.admitted = True

    def settle(self, op, deadline):
        cleanup_authorized = self.admitted
        self.admitted = False  # Disarm before normal close or an explicit abort can fail.
        self.settled = True
        if op == "abort" and cleanup_authorized:
            self.aborted = True
            self.targets.abort(deadline)
        elif op == "close":
            self.targets.poll()
        self.targets.close()

    def action(self, op, deadline):
        if not self.admitted or self.aborted:
            raise RuntimeError("Lifecycle is not admitted/active")
        if op == "terminateApp":
            self.targets.poll()
            if not self.targets.exited["worker"]:
                raise RuntimeError("Worker exit required before App termination")
            self.targets.terminate("app", deadline)
        elif op == "terminateWorker":
            self.targets.terminate("worker", deadline)

    def dispatch(self, request):
        self.sequence += 1
        if type(request) is not dict or request.get("seq") != self.sequence:
            raise RuntimeError("Lifecycle sequence mismatch")
        op = request.get("op")
        if op == "clock":
            exact(request, ("seq", "op"))
            self.last_clock = now()
            self.reply(op, min(self.hard_deadline - RESERVE_NS, now() + PHASE_NS), self.last_clock)
            return
        deadline = self.phase_deadline(request, op)
        if op == "admit":
            self.admit(request, deadline)
        elif op in ("close", "abort"):
            self.settle(op, deadline)
        elif op in ("observe", "terminateWorker", "terminateApp"):
            self.action(op, deadline)
        else:
            raise RuntimeError("Unknown fixed lifecycle operation")
        self.reply(op, deadline)

    def cleanup(self):
        errors = []
        try:
            if self.admitted and now() < self.hard_deadline - CLOSE_NS:
                # One originally reserved phase only, never renewed after expiry.
                self.targets.abort(min(self.hard_deadline - CLOSE_NS, now() + RESERVE_NS - CLOSE_NS))
        except Exception as error:
            errors.append(error)
        try:
            if not self.targets.closed:
                self.targets.close()
        except Exception as error:
            errors.append(error)
        return errors


def run():
    if sys.platform != "linux" or len(sys.argv) != 1:
        raise RuntimeError("Expected isolated Linux pidfd helper")
    session = Session()
    primary = None
    try:
        session.open()
        while not session.settled:
            request = session.transport.read(session.hard_deadline - RESERVE_NS)
            session.dispatch(request)
    except (Exception, KeyboardInterrupt) as error:
        primary = error
    finally:
        cleanup_errors = session.cleanup()
    if primary is not None or cleanup_errors:
        raise LifecycleFailure(([primary] if primary is not None else []) + cleanup_errors, session.targets.observation())

if __name__ == "__main__":
    try:
        run()
    except (Exception, KeyboardInterrupt) as failure:
        # Fixed, bounded diagnostics: no mutable process argv or terminal bytes.
        diagnostic = {"errors": failure_records(failure), "observation": failure.observation if isinstance(failure, LifecycleFailure) else None}
        raw = (json.dumps(diagnostic, separators=(",", ":")) + "\n").encode("ascii")
        os.write(2, raw[:4096])
        raise SystemExit(1) from None
