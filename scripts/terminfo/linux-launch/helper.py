"""Unwired Linux source-owned App launch. No worker/desktop/whole-run qualification."""
import errno
import hashlib
import json
import os
import platform
import resource
import select
import signal
import struct
import sys
import time

FRAME = 4096
LOG_LIMIT = 1048576
RESERVE = 4_000_000_000
CLOSE_RESERVE = 250_000_000
PHASE = 4_000_000_000
STOP_REQUESTED = False


def now():
    """Return this helper's monotonic sample; never assume it shares Node's epoch."""
    return time.monotonic_ns()


def exact(value, names):
    """Reject unknown or missing protocol fields before any process action."""
    if not isinstance(value, dict) or set(value) != set(names):
        raise ValueError("Fields")
    return value


def integer(value, low, high):
    """Admit a bounded JSON integer while refusing bool-as-int coercion."""
    if isinstance(value, bool) or not isinstance(value, int) or not low <= value <= high:
        raise ValueError("Integer")
    return value


def unique(pairs):
    """Decode JSON objects without silently accepting duplicate keys."""
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("DuplicateField")
        value[key] = item
    return value


def deadline(value, ceiling=None):
    """Validate a canonical helper-clock absolute deadline with a finite ceiling."""
    if not isinstance(value, str) or not value.isascii() or not value.isdecimal() or (len(value) > 1 and value[0] == "0") or len(value) > 20:
        raise ValueError("Deadline")
    limit = int(value)
    if limit <= now() or limit - now() > (PHASE if ceiling is None else ceiling):
        raise ValueError("Deadline")
    return limit


def check(limit):
    """Refuse work after its absolute deadline; synchronous syscalls are not preempted."""
    if limit is not None and now() >= limit:
        raise TimeoutError("Expired")


def identity(stat):
    """Project lossless regular-file identity, including mutation timestamps."""
    return dict(zip(("dev", "ino", "rdev", "uid", "mode", "size", "mtimeNs", "ctimeNs"),
                    map(str, (stat.st_dev, stat.st_ino, stat.st_rdev, stat.st_uid, stat.st_mode,
                              stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns))))


def stable(stat):
    """Project directory identity without treating legitimate entry writes as replacement."""
    return dict(zip(("dev", "ino", "uid", "mode"), map(str, (stat.st_dev, stat.st_ino, stat.st_uid, stat.st_mode))))


def digest(fd, maximum, limit=None):
    """Hash only held, bounded file bytes within the current phase budget."""
    check(limit)
    stat = os.fstat(fd)
    if not 1 <= stat.st_size <= maximum:
        raise ValueError("FileSize")
    result = hashlib.sha256()
    offset = 0
    while offset < stat.st_size:
        check(limit)
        data = os.pread(fd, min(65536, stat.st_size - offset), offset)
        if not data:
            raise ValueError("ShortFile")
        result.update(data)
        offset += len(data)
    if identity(os.fstat(fd)) != identity(stat):
        raise ValueError("FileChanged")
    check(limit)
    return result.hexdigest()


class Transport:
    def __init__(self):
        self.buffer = bytearray()
        self.commands = 0
        self.sequence = 0
        self.eof = False
        os.set_blocking(0, False)
        os.set_blocking(1, False)

    def receive(self):
        """Parse at most one bounded, strictly sequenced coordinator frame."""
        if b"\n" in self.buffer:
            line, _, rest = self.buffer.partition(b"\n")
            self.buffer = bytearray(rest)
            self.commands += 1
            if self.commands > 64:
                raise ValueError("CommandLimit")
            value = json.loads(line, object_pairs_hook=unique)
            if not isinstance(value, dict) or value.get("seq") != self.sequence + 1:
                raise ValueError("Sequence")
            self.sequence += 1
            return value
        try:
            data = os.read(0, FRAME + 1)
        except BlockingIOError:
            return None
        if not data:
            self.eof = True
            return None
        if len(data) > FRAME - len(self.buffer):
            raise ValueError("FrameLimit")
        self.buffer.extend(data)
        return self.receive() if b"\n" in self.buffer else None

    def send(self, value, limit):
        """Bound and finish one framed response before its absolute write deadline."""
        raw = (json.dumps(value, separators=(",", ":")) + "\n").encode()
        if len(raw) > FRAME:
            raise ValueError("ResponseLimit")
        offset = 0
        while offset < len(raw):
            check(limit)
            _, writable, _ = select.select([], [1], [], min(0.05, (limit - now()) / 1e9))
            if writable:
                try:
                    offset += os.write(1, raw[offset:])
                except BlockingIOError:
                    pass


def initial(transport, limit=None):
    """Read exactly the first bounded configuration frame before the startup cutoff."""
    limit = limit if limit is not None else now() + PHASE
    while True:
        check(limit)
        frame = transport.receive()
        if frame is not None:
            exact(frame, ("seq", "op", "runId", "uid", "appPath", "appDirectory", "scratch", "appData", "home", "locale", "appVersion", "pins", "files", "directories"))
            if frame["op"] != "init":
                raise ValueError("InitialOperation")
            return frame
        if transport.eof:
            raise EOFError("InitialEof")
        select.select([0], [], [], 0.01)


def runtime_check():
    """Require the reviewed Linux CPython primitives and disable inherited auto-reaping."""
    if sys.platform != "linux" or sys.implementation.name != "cpython" or not (3, 12) <= sys.version_info[:2] <= (3, 14):
        raise RuntimeError("Runtime")
    for name in ("fork", "pidfd_open", "waitid", "P_PIDFD", "WNOWAIT", "WEXITED", "WNOHANG"):
        if not hasattr(os, name):
            raise RuntimeError("Unsupported")
    if os.execve not in os.supports_fd or not hasattr(signal, "pidfd_send_signal"):
        raise RuntimeError("Unsupported")
    # Reviewed CPython uses a fresh sigaction; SIG_IGN/SA_NOCLDWAIT cannot survive.
    signal.signal(signal.SIGCHLD, signal.SIG_DFL)


def file_check(config, initial_settings=False, limit=None):
    """Recheck held artifact bytes/directories, with initial-only settings admission."""
    check(limit)
    uid = config["uid"]
    for fd, name, maximum in ((6, "app", 33554432), (11, "managed", 67108864), (12, "pty", 16777216)):
        check(limit)
        stat = os.fstat(fd)
        if identity(stat) != config["files"][name] or stat.st_uid != uid or stat.st_mode & 0o6022 or stat.st_nlink != 1:
            raise ValueError("ArtifactIdentity")
        if digest(fd, maximum, limit) != config["pins"][name]:
            raise ValueError("ArtifactHash")
    header = os.pread(6, 20, 0)
    machine = {"x86_64": 62, "aarch64": 183}.get(platform.machine())
    if len(header) != 20 or header[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", header, 18)[0] != machine:
        raise ValueError("AppElf")
    if initial_settings and (identity(os.fstat(9)) != config["files"]["settings"] or digest(9, 65536, limit) != config["pins"]["settings"]):
        raise ValueError("InitialSettings")
    for fd, name in ((7, "app"), (10, "scratch"), (13, "appData"), (14, "home")):
        check(limit)
        stat = os.fstat(fd)
        if stable(stat) != config["directories"][name] or stat.st_uid != uid or stat.st_mode & 0o177777 != 0o40700:
            raise ValueError("DirectoryIdentity")


def config_run_paths(config):
    """Bind private paths and fixed launch markers to this uniform-UID helper."""
    uid = integer(config["uid"], 0, 4294967294)
    run = config["runId"]
    if any(value != uid for value in os.getresuid()) or not isinstance(run, str) or len(run) != 32 or any(c not in "0123456789abcdef" for c in run):
        raise ValueError("RunIdentity")
    if sys.argv[1:] != ["--run-id=" + run]:
        raise ValueError("RunMarker")
    if config["appPath"] != config["appDirectory"] + "/Ntilde" or config["appData"] != config["scratch"] + "/appdata" or config["home"] != config["scratch"] + "/home":
        raise ValueError("FixedPaths")
    for name in ("appPath", "appDirectory", "scratch", "appData", "home"):
        value = config[name]
        if not isinstance(value, str) or len(value) >= 4096 or not value.startswith("/") or any(ord(c) < 32 or ord(c) == 127 for c in value):
            raise ValueError("Path")


def config_pins(config):
    """Require only the fixed sidecar files and canonical SHA256 expectations."""
    exact(config["pins"], ("app", "managed", "pty", "settings"))
    if any(not isinstance(v, str) or len(v) != 64 or any(c not in "0123456789abcdef" for c in v) for v in config["pins"].values()):
        raise ValueError("Pins")
    exact(config["files"], ("app", "managed", "pty", "settings"))
    exact(config["directories"], ("app", "scratch", "appData", "home"))


def config_environment(config):
    """Admit bounded declared locale/version values, never an inherited environment."""
    if not isinstance(config["locale"], str) or len(config["locale"]) > 64 or not config["locale"].lower().endswith(("utf-8", "utf8")):
        raise ValueError("Locale")
    if not isinstance(config["appVersion"], str) or len(config["appVersion"]) > 64:
        raise ValueError("Version")


def config_stdio(config):
    """Verify the fixed null input and fresh private App-log descriptor."""
    null = os.fstat(8)
    log = os.fstat(3)
    if null.st_rdev != os.makedev(1, 3) or null.st_mode & 0o170000 != 0o20000 or null.st_uid != 0:
        raise ValueError("NullDevice")
    if log.st_mode & 0o177777 != 0o100600 or log.st_uid != config["uid"] or log.st_size != 0 or log.st_nlink != 1:
        raise ValueError("PrivateLog")


def config_check(config, limit=None):
    """Complete resource and input admission before creating any child."""
    config_run_paths(config)
    config_pins(config)
    config_environment(config)
    config_stdio(config)
    file_check(config, True, limit)


class Child:
    def __init__(self, config):
        self.config = config
        self.pid = None
        self.pidfd = None
        self.gate = None
        self.output = None
        self.exec_error = None
        self.error_bytes = bytearray()
        self.log_close_attempted = False
        self.hard_deadline = None
        self.state = {"childPid": None, "forked": False, "pidfdOpened": False, "preExecReady": False, "gateReleased": False,
                      "execPipeClosed": False, "admitted": False, "exitObserved": False, "childReaped": False,
                      "preExecChildReaped": False, "appReaped": False, "wait": None, "childErrno": None,
                      "attempts": [], "pidfdClosed": False, "childPipesClosed": False, "appLogClosed": False,
                      "appLogBytes": 0, "aborted": False, "errors": []}

    def abort(self, code):
        """Use only the preauthorized remaining abort reserve and retain cleanup failures."""
        self.state["aborted"] = True
        if code not in self.state["errors"] and len(self.state["errors"]) < 8:
            self.state["errors"].append(code)

    def child_main(self, gate, output, error, limit):
        """Isolate the child, publish setup readiness, then await the sole finite exec gate."""
        try:
            os.setsid()
            os.dup2(8, 0)
            os.set_blocking(output, True)
            os.dup2(output, 1)
            os.dup2(output, 2)
            os.fchdir(7)
            keep = sorted((6, gate, error))
            start = 3
            for fd in keep:
                os.closerange(start, fd)
                start = fd + 1
            os.closerange(start, min(resource.getrlimit(resource.RLIMIT_NOFILE)[0], 1048576))
            for sig in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP, signal.SIGPIPE, signal.SIGCHLD):
                signal.signal(sig, signal.SIG_DFL)
            check(limit)
            if os.write(error, b"R") != 1:
                os._exit(126)
            ready, _, _ = select.select([gate], [], [], max(0, (limit - now()) / 1e9))
            if not ready or os.read(gate, 2) != b"G":
                os._exit(125)
            check(limit)
            os.close(gate)
            config = self.config
            environment = {"HOME": config["home"], "PATH": "/usr/bin:/bin", "LANG": config["locale"],
                           "LC_CTYPE": config["locale"], "TERM_PROGRAM": "Ntilde", "TERM_PROGRAM_VERSION": config["appVersion"],
                           "TERMINFO_RUN_ID": config["runId"], "NTILDE_APPDATA_ROOT": config["appData"]}
            argv = [config["appPath"], "--ntilde-terminfo-owner=" + config["runId"], "--ntilde-terminfo-private-clipboard=" + config["runId"]]
            os.set_inheritable(6, False)
            os.execve(6, argv, environment)
        except Exception as ex:
            value = ex.errno if isinstance(ex, OSError) and ex.errno else errno.EINVAL
            try:
                os.write(error, b"E" + struct.pack("<I", value))
            except OSError:
                pass
            os._exit(126)

    def active_limit(self, limit=None):
        """Clamp every child operation to the original, nonrenewable hard lifetime."""
        if self.hard_deadline is None:
            return limit
        return self.hard_deadline if limit is None else min(limit, self.hard_deadline)

    def guard(self, limit=None):
        """Enforce the tighter phase/lifetime cutoff immediately around child work."""
        value = self.active_limit(limit)
        if value is not None:
            check(value)
        return value

    def prepare(self, limit, lifetime=None):
        """Fork once and hold the original pidfd before accepting child setup readiness."""
        if self.pid is not None:
            raise ValueError("RepeatedFork")
        self.hard_deadline = lifetime if lifetime is not None else limit
        file_check(self.config, True, limit)
        check(limit)
        opened = []
        try:
            gate_read, gate_write = os.pipe2(os.O_CLOEXEC)
            opened.extend((gate_read, gate_write))
            out_read, out_write = os.pipe2(os.O_CLOEXEC | os.O_NONBLOCK)
            opened.extend((out_read, out_write))
            err_read, err_write = os.pipe2(os.O_CLOEXEC | os.O_NONBLOCK)
            opened.extend((err_read, err_write))
            child = os.fork()
        except BaseException:
            for fd in opened:
                try:
                    os.close(fd)
                except OSError:
                    self.abort("PreparationFdClose")
            raise
        if child == 0:
            self.child_main(gate_read, out_write, err_write, limit)
            os._exit(126)
        self.pid = child
        self.state.update(childPid=child, forked=True)
        self.gate, self.output, self.exec_error = gate_write, out_read, err_read
        for fd in (gate_read, out_write, err_write):
            try:
                os.close(fd)
            except OSError:
                self.abort("DuplicatePipeClose")
        try:
            self.pidfd = os.pidfd_open(child, 0)
            self.state["pidfdOpened"] = True
        except OSError:
            self.abort("PidfdAcquisition")
            self.close_gate()
            self.reap_preexec(limit)
            raise
        self.await_ready(limit)
        check(limit)

    def await_ready(self, limit):
        """Wait only for bounded setup readiness while refusing early death/error/expiry."""
        while not self.state["preExecReady"]:
            check(limit)
            self.pump_exec()
            self.poll()
            if self.state["exitObserved"] or self.state["aborted"]:
                raise RuntimeError("PreExecReady")
            if not self.state["preExecReady"]:
                time.sleep(0.001)

    def close_gate(self):
        """Close the sole parent gate writer once; unreleased children must not exec."""
        self.close_pipe("gate")

    def close_pipe(self, name):
        """Discard a known pipe once while preserving any unconfirmed close as an abort."""
        value = getattr(self, name)
        setattr(self, name, None)
        if value is not None:
            try:
                os.close(value)
            except OSError:
                self.abort("ChildPipeClose")
                raise

    def pump_log(self, limit=None):
        """Drain private App output while checking the cap before retention or file writes."""
        self.guard(limit)
        if self.output is None:
            return
        try:
            data = os.read(self.output, 65536)
        except BlockingIOError:
            return
        if not data:
            self.close_pipe("output")
            return
        if "AppLogOverflow" in self.state["errors"]:
            return
        remaining = LOG_LIMIT - self.state["appLogBytes"]
        if len(data) > remaining:
            self.abort("AppLogOverflow")
            return
        offset = 0
        while offset < len(data):
            self.guard(limit)
            count = os.write(3, data[offset:])
            if count <= 0:
                raise OSError("LogWrite")
            offset += count
            self.state["appLogBytes"] += count
            self.guard(limit)

    def pump_exec(self):
        """Retain setup readiness and errno/EOF facts without inferring App execution."""
        if self.exec_error is None:
            return
        try:
            data = os.read(self.exec_error, 7)
        except BlockingIOError:
            return
        if len(data) > 6 - len(self.error_bytes):
            raise ValueError("ExecErrorLimit")
        self.error_bytes.extend(data)
        if self.error_bytes[:1] == b"R" and not self.state["preExecReady"]:
            self.state["preExecReady"] = True
            del self.error_bytes[:1]
        if self.error_bytes and self.error_bytes[:1] != b"E":
            raise ValueError("ChildErrorPrefix")
        if not data:
            self.close_pipe("exec_error")
            self.state["execPipeClosed"] = True
            if self.error_bytes:
                if len(self.error_bytes) != 5:
                    raise ValueError("ExecErrorFrame")
                self.state["childErrno"] = struct.unpack("<I", self.error_bytes[1:])[0]
                self.abort("ChildSetupOrExecFailed")
            elif not self.state["preExecReady"]:
                self.abort("PreExecUnready")

    def poll(self):
        """Observe only the original pidfd; readiness never supplies a consuming wait."""
        if self.pidfd is None:
            return
        poller = select.poll()
        poller.register(self.pidfd, select.POLLIN | select.POLLHUP | select.POLLERR | select.POLLNVAL)
        events = poller.poll(0)
        if any(mask & (select.POLLERR | select.POLLNVAL) for _, mask in events):
            raise RuntimeError("PidfdPoll")
        if events:
            self.state["exitObserved"] = True

    def reap(self, limit=None):
        """Record a genuine consuming wait for this exact original direct child."""
        self.guard(limit)
        if self.pidfd is None or self.state["childReaped"]:
            return
        result = os.waitid(os.P_PIDFD, self.pidfd, os.WEXITED | os.WNOHANG)
        if not result:
            self.guard(limit)
            return
        if result.si_pid != self.pid or result.si_code not in (os.CLD_EXITED, os.CLD_KILLED, os.CLD_DUMPED):
            raise RuntimeError("WaitIdentity")
        self.state["wait"] = {"source": "P_PIDFD", "pid": result.si_pid, "uid": result.si_uid,
                              "code": result.si_code, "status": result.si_status}
        self.state.update(childReaped=True, exitObserved=True)
        self.state["appReaped"] = self.state["admitted"]
        self.state["preExecChildReaped"] = not self.state["gateReleased"]
        self.guard(limit)

    def reap_preexec(self, limit):
        """Reap only the original never-released child when first pidfd acquisition failed."""
        while not self.state["childReaped"]:
            check(limit)
            child, status = os.waitpid(self.pid, os.WNOHANG)
            if child == self.pid:
                self.state["wait"] = {"source": "waitpid-preexec", "pid": child, "uid": None, "code": None, "status": status}
                self.state.update(childReaped=True, preExecChildReaped=True, exitObserved=True)
                check(limit)
                return
            time.sleep(0.001)

    def pump(self, unexpected=True, limit=None):
        """Collect bounded pipe/exit/wait observations and mark unexpected exit as fatal."""
        self.guard(limit)
        self.pump_log(limit)
        self.pump_exec()
        self.guard(limit)
        self.poll()
        self.reap(limit)
        if unexpected and self.state["exitObserved"]:
            self.abort("UnexpectedChildExit")

    def release(self, limit):
        """Release exactly one exec gate only while the original reference and pins remain valid."""
        if self.gate is None or self.state["gateReleased"] or self.state["aborted"]:
            raise ValueError("GateState")
        self.pump(limit=limit)
        if self.state["exitObserved"]:
            raise ValueError("GateChildExited")
        file_check(self.config, limit=limit)
        check(limit)
        if os.write(self.gate, b"G") != 1:
            raise RuntimeError("GateWrite")
        self.state["gateReleased"] = True
        self.close_gate()
        while not self.state["execPipeClosed"]:
            check(limit)
            self.pump(limit=limit)
            time.sleep(0.001)
        if self.state["aborted"]:
            raise RuntimeError("ExecUnavailable")

    def send(self, sig, limit):
        """Attempt TERM/KILL at most once each through the original pidfd, never a PID."""
        limit = self.active_limit(limit)
        check(limit)
        if self.pidfd is None or self.state["exitObserved"]:
            return
        name = "TERM" if sig == signal.SIGTERM else "KILL"
        if any(attempt["signal"] == name for attempt in self.state["attempts"]):
            return
        item = {"signal": name, "sent": False, "errno": None}
        try:
            signal.pidfd_send_signal(self.pidfd, sig)
            item["sent"] = True
        except OSError as ex:
            item["errno"] = ex.errno
            self.abort("SignalFailed")
        self.state["attempts"].append(item)
        check(limit)

    def cleanup_pump(self, limit):
        """Retain observation failures without blocking original-reference signals or waits."""
        self.guard(limit)
        observations = ((lambda: self.pump_log(limit), "CleanupLog"), (self.pump_exec, "CleanupExec"),
                        (self.poll, "CleanupPoll"), (lambda: self.reap(limit), "CleanupWait"))
        for operation, code in observations:
            try:
                operation()
            except TimeoutError:
                raise
            except (OSError, ValueError, RuntimeError):
                self.abort(code)
            self.guard(limit)

    def stop(self, limit, require_signal=False):
        """Share one deadline across gate closure, fixed signals and genuine owner waiting."""
        limit = self.active_limit(limit)
        check(limit)
        try:
            self.close_gate()
        except OSError:
            self.abort("ChildPipeClose")
        self.cleanup_pump(limit)
        if self.pid is None:
            return
        if self.pidfd is None:
            self.reap_preexec(limit)
            return
        if not self.state["exitObserved"]:
            self.send(signal.SIGTERM, limit)
        if require_signal and not any(item["sent"] for item in self.state["attempts"]):
            self.abort("IndependentExitBeforeStop")
        grace = min(limit - CLOSE_RESERVE, now() + min(1_000_000_000, max(0, (limit - now()) // 2)))
        while not self.state["childReaped"] and now() < grace:
            self.cleanup_pump(limit)
            time.sleep(0.001)
        if not self.state["exitObserved"]:
            self.send(signal.SIGKILL, limit)
        while not self.state["childReaped"]:
            check(limit - CLOSE_RESERVE)
            self.cleanup_pump(limit)
            time.sleep(0.001)

    def close(self):
        """Attempt known descriptor closure once and preserve every unconfirmed close."""
        for name in ("gate", "output", "exec_error"):
            value = getattr(self, name)
            setattr(self, name, None)
            if value is not None:
                try:
                    os.close(value)
                except OSError:
                    self.abort("ChildPipeClose")
        self.state["childPipesClosed"] = not any(code in ("ChildPipeClose", "PreparationFdClose", "DuplicatePipeClose") for code in self.state["errors"])
        if self.pidfd is not None:
            value, self.pidfd = self.pidfd, None
            try:
                os.close(value)
            except OSError:
                self.abort("PidfdClose")
        self.state["pidfdClosed"] = "PidfdClose" not in self.state["errors"]
        if not self.log_close_attempted:
            self.log_close_attempted = True
            try:
                os.close(3)
                self.state["appLogClosed"] = True
            except OSError:
                self.abort("AppLogClose")


class Session:
    def __init__(self, transport, config, startup=None):
        self.transport = transport
        self.child = Child(config)
        self.lifetime = None
        self.done = False
        self.startup_deadline = startup if startup is not None else now() + PHASE

    def response(self, sequence, limit, clock=False):
        """Write a bounded correlated observation; its flags are not a receipt."""
        value = {"seq": sequence, "ok": not self.child.state["aborted"], "observation": self.child.state}
        if clock:
            value["clockNs"] = str(now())
        self.transport.send(value, limit)

    def prepare_command(self, frame, limit):
        """Establish the original lifetime once before any fork operation."""
        if self.lifetime is not None:
            raise ValueError("RepeatedPrepare")
        self.lifetime = deadline(frame["lifetimeDeadlineNs"], 300_000_000_000)
        if self.lifetime - now() < 10_000_000_000 - PHASE:
            raise ValueError("Lifetime")
        self.child.prepare(limit, self.lifetime)

    def admit_command(self, limit):
        """Accept the coordinator's post-provider admission only for the still-live original."""
        self.child.pump(limit=limit)
        state = self.child.state
        if not state["pidfdOpened"] or not state["preExecReady"] or not state["gateReleased"] or not state["execPipeClosed"] or state["aborted"]:
            raise ValueError("AdmissionState")
        check(limit)
        state["admitted"] = True

    def finish_command(self, abort, limit):
        """Stop/close the created child without converting explicit abort to normal success."""
        if abort:
            self.child.abort("ExplicitAbort")
        self.child.stop(min(limit, self.lifetime or limit), require_signal=not abort)
        self.child.close()
        self.done = True

    def command_limit(self, operation, limit):
        """Keep ordinary work outside the original abort reserve, including clock responses."""
        if operation in ("stop", "abort"):
            limit = min(limit, self.lifetime or limit)
        else:
            cutoff = self.lifetime - RESERVE if self.lifetime is not None else self.startup_deadline
            limit = min(limit, cutoff)
        check(limit)
        return limit

    def command(self, frame):
        """Dispatch only the fixed sequenced protocol operations and their exact fields."""
        operation = frame.get("op")
        if operation == "clock":
            exact(frame, ("seq", "op"))
            self.response(frame["seq"], self.command_limit(operation, now() + PHASE), True)
            return
        expected = ("seq", "op", "deadlineNs", "lifetimeDeadlineNs") if operation == "prepare" else ("seq", "op", "deadlineNs")
        exact(frame, expected)
        limit = self.command_limit(operation, deadline(frame["deadlineNs"]))
        operations = {"prepare": lambda: self.prepare_command(frame, limit), "release": lambda: self.child.release(limit),
                      "admitApp": lambda: self.admit_command(limit), "observe": lambda: self.child.pump(limit=limit),
                      "stop": lambda: self.finish_command(False, limit), "abort": lambda: self.finish_command(True, limit)}
        if operation not in operations:
            raise ValueError("Operation")
        operations[operation]()
        self.response(frame["seq"], limit)

    def abort(self, code):
        """Use only the preauthorized remaining abort reserve and retain cleanup failures."""
        self.child.abort(code)
        limit = min(now() + RESERVE, self.lifetime or now() + RESERVE)
        try:
            self.child.stop(limit)
        except Exception:
            self.child.abort("AbortCleanup")
        finally:
            self.child.close()
        try:
            self.response(None, max(now() + 1, limit))
        except Exception:
            pass
        self.done = True

    def expiry_reason(self):
        """Select the source-owned startup/ordinary cutoff without renewing it."""
        if STOP_REQUESTED:
            return "HelperInterrupted"
        if self.lifetime is None and now() >= self.startup_deadline:
            return "StartupExpired"
        if self.lifetime is not None and now() >= self.lifetime - RESERVE:
            return "LifetimeExpired"
        return None

    def step(self):
        """Watch control EOF and child events between commands, including when idle."""
        frame = self.transport.receive()
        if self.transport.eof:
            self.abort("CoordinatorEof")
            return
        if frame is not None:
            self.command(frame)
            return
        limit = self.lifetime - RESERVE if self.lifetime is not None else self.startup_deadline
        self.child.pump(limit=limit)
        if self.child.state["aborted"]:
            self.abort("ChildUnavailable")
            return
        select.select([0], [], [], 0.005)

    def run(self):
        """Keep watching until settlement; coordinator EOF can never release a live lease."""
        while not self.done:
            reason = self.expiry_reason()
            if reason:
                self.abort(reason)
                break
            self.step()


def interrupted(_sig, _frame):
    """Latch helper interruption for bounded cleanup outside the signal callback."""
    global STOP_REQUESTED
    STOP_REQUESTED = True


def main():
    """Run the isolated fixed-descriptor helper; actual App/desktop qualification is absent."""
    session = None
    try:
        startup = now() + PHASE
        runtime_check()
        os.setsid()
        for sig in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            signal.signal(sig, interrupted)
        transport = Transport()
        config = initial(transport, startup)
        config_check(config, startup)
        session = Session(transport, config, startup)
        session.response(1, startup, True)
        session.run()
        return 0
    except Exception as error:
        if session is not None:
            session.abort("HelperFailure")
        try:
            os.write(2, ("launch helper failure: " + type(error).__name__ + ": " + str(error))[:256].encode("utf-8", "replace") + b"\n")
        except OSError:
            pass
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
