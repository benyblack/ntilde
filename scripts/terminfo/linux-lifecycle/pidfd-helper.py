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
            end = self.buffer.find(b"\n")
            if end >= 0:
                if end + 1 > MAX_FRAME:
                    raise RuntimeError("Lifecycle input frame too large")
                raw = bytes(self.buffer[:end])
                del self.buffer[:end + 1]
                self.frames += 1
                if self.frames > MAX_COMMANDS:
                    raise RuntimeError("Lifecycle command limit exhausted")
                return json.loads(raw.decode("utf-8", "strict"), object_pairs_hook=unique)
            ready, _, _ = select.select([0], [], [], max(0, (deadline - now()) / 1e9))
            if not ready:
                continue
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


class Targets:
    def __init__(self):
        self.fds = {}
        self.exited = {"app": False, "worker": False}
        self.events = {"app": 0, "worker": 0}
        self.attempts = []
        self.closed = False
        self.close_attempted = False

    def open(self, app, worker):
        try:
            for role, pid in (("app", app), ("worker", worker)):
                self.fds[role] = os.pidfd_open(pid, 0)
            self.poll()
            if any(self.exited.values()):
                raise RuntimeError("Original target already exited")
        except BaseException:
            self.close()
            raise

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
            except (OSError, RuntimeError, TimeoutError) as error:
                errors.append(type(error).__name__)
        if errors:
            raise RuntimeError("Original-target abort failed: " + ",".join(errors))

    def close(self):
        if self.close_attempted:
            return  # Never retry a numeric FD whose failed close may already have freed it.
        self.close_attempted = True
        errors = []
        for fd in self.fds.values():
            try:
                os.close(fd)
            except OSError as error:
                errors.append(error)
        self.fds.clear()
        self.closed = not errors
        if errors:
            raise RuntimeError("Original pidfd close unconfirmed")


class LifecycleFailure(RuntimeError):
    def __init__(self, errors, observation):
        self.errors = errors
        self.observation = observation


def run():
    if sys.platform != "linux" or len(sys.argv) != 1:
        raise RuntimeError("Expected isolated Linux pidfd helper")
    transport, targets = Transport(), Targets()
    admitted = False
    aborted = False
    settled = False
    hard_deadline = now() + PHASE_NS
    last_clock = None
    sequence = 0
    primary = None
    cleanup_errors = []
    try:
        opening = transport.read(hard_deadline)
        exact(opening, ("op", "seq", "appPid", "workerPid", "lifetimeMs"))
        if opening["op"] != "open" or opening["seq"] != 1:
            raise RuntimeError("Expected initial lifecycle open")
        app = integer(opening["appPid"], 2, 2147483647)
        worker = integer(opening["workerPid"], 2, 2147483647)
        if app == worker:
            raise RuntimeError("Distinct original roles required")
        lifetime = integer(opening["lifetimeMs"], 10000, 300000)
        targets.open(app, worker)
        hard_deadline = now() + lifetime * 1_000_000
        sequence = 1
        transport.write({"seq": sequence, "kind": "opened", "observation": targets.observation()}, min(hard_deadline, now() + PHASE_NS))
        while True:
            request = transport.read(hard_deadline - RESERVE_NS)
            sequence += 1
            if type(request) is not dict or request.get("seq") != sequence:
                raise RuntimeError("Lifecycle sequence mismatch")
            op = request.get("op")
            if op == "clock":
                exact(request, ("seq", "op"))
                last_clock = now()
                transport.write({"seq": sequence, "kind": "clock", "clockNs": str(last_clock),
                                 "observation": targets.observation()}, min(hard_deadline - RESERVE_NS, now() + PHASE_NS))
                continue
            exact(request, ("seq", "op", "deadlineNs", "lifetimeDeadlineNs") if op == "admit" else ("seq", "op", "deadlineNs"))
            raw = request["deadlineNs"]
            if type(raw) is not str or not raw.isascii() or not raw.isdecimal() or len(raw) > 20:
                raise RuntimeError("Invalid helper absolute deadline")
            deadline = int(raw)
            bound = hard_deadline - (CLOSE_NS if op in ("abort", "close") else RESERVE_NS)
            if last_clock is None or not now() < deadline <= min(bound, last_clock + PHASE_NS):
                raise TimeoutError("Expired/unarmed lifecycle phase")
            last_clock = None
            if op == "admit":
                if admitted or aborted:
                    raise RuntimeError("Lifecycle admission is one-shot")
                lifetime_raw = request["lifetimeDeadlineNs"]
                if type(lifetime_raw) is not str or not lifetime_raw.isascii() or not lifetime_raw.isdecimal() or len(lifetime_raw) > 20:
                    raise RuntimeError("Invalid conservative lifecycle lifetime")
                hard_deadline = min(hard_deadline, int(lifetime_raw))
                if deadline >= hard_deadline - RESERVE_NS:
                    raise RuntimeError("Lifecycle lifetime has no cleanup reserve")
                targets.poll()
                if any(targets.exited.values()):
                    raise RuntimeError("Original target exited across admission")
                admitted = True
            elif op == "close":
                # Critical: normal close must not turn its own EOF into target cleanup.
                admitted = False
                settled = True
                targets.poll()
                targets.close()
            elif op == "abort":
                cleanup_authorized = admitted
                admitted = False  # This explicit phase must not restart on failure in finally.
                settled = True
                if cleanup_authorized:
                    aborted = True
                    targets.abort(deadline)
                targets.close()
            elif op in ("observe", "terminateWorker", "terminateApp"):
                if not admitted or aborted:
                    raise RuntimeError("Lifecycle is not admitted/active")
                if op == "terminateApp":
                    targets.poll()
                    if not targets.exited["worker"]:
                        raise RuntimeError("Worker exit required before App termination")
                    targets.terminate("app", deadline)
                elif op == "terminateWorker":
                    targets.terminate("worker", deadline)
            else:
                raise RuntimeError("Unknown fixed lifecycle operation")
            transport.write({"seq": sequence, "kind": op, "observation": targets.observation()}, deadline)
            if settled:
                break
    except BaseException as error:
        primary = error
    finally:
        try:
            if admitted and now() < hard_deadline - CLOSE_NS:
                # Originally authorized reserve only; never extend the lifetime on EOF.
                targets.abort(min(hard_deadline - CLOSE_NS, now() + RESERVE_NS - CLOSE_NS))
        except BaseException as error:
            cleanup_errors.append(error)
        finally:
            if not targets.closed:
                try:
                    targets.close()
                except BaseException as error:
                    cleanup_errors.append(error)
    if primary is not None or cleanup_errors:
        # Keep primary failure and actual cleanup attempts even if cleanup itself failed.
        raise LifecycleFailure(([primary] if primary is not None else []) + cleanup_errors, targets.observation())


if __name__ == "__main__":
    try:
        run()
    except (Exception, KeyboardInterrupt) as failure:
        # Fixed, bounded diagnostics: no mutable process argv or terminal bytes.
        failures = failure.errors if isinstance(failure, LifecycleFailure) else [failure]
        errors = [{"type": type(error).__name__, "message": str(error).encode("ascii", "backslashreplace")[:256].decode("ascii")} for error in failures[:4]]
        diagnostic = {"errors": errors, "observation": failure.observation if isinstance(failure, LifecycleFailure) else None}
        raw = (json.dumps(diagnostic, separators=(",", ":")) + "\n").encode("ascii")
        os.write(2, raw[:4096])
        raise SystemExit(1) from None
