"""Collect two identity OSCs inside a fresh, network-isolated Linux container."""

from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import stat
import struct
import subprocess
import sys
import tempfile
import time

SOURCE = Path(__file__).parent
PYTHON = "/usr/bin/python3"
BASE_ID = "qa.foot.baseline"
WANTED_ID = "terminfo-test"


def need(condition, message):
    if not condition:
        raise RuntimeError(message)


def put(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def read(path):
    return json.loads(path.read_text())


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def command(args, env):
    return subprocess.run(args, env=env, capture_output=True, text=True, timeout=8, check=True).stdout


def wait_for(check, description, process=None):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if process is not None:
            need(process.poll() is None, description + " process exited: " + str(process.returncode))
        value = check()
        if value is not None:
            return value
        time.sleep(0.06)
    raise RuntimeError("timed out waiting for " + description)


def single(items):
    values = list(items)
    need(len(values) <= 1, "ambiguous private session object")
    return next(iter(values), None)


def process_facts(pid):
    proc = Path("/proc", str(pid))
    fields = (proc / "stat").read_text().rsplit(") ", 1)[1].split()
    need(fields[0] != "Z", "owned process is a zombie")
    return {"pid": pid, "parent": int(fields[1]), "session": int(fields[3]), "start": fields[19],
            "uid": proc.stat().st_uid, "exe": str((proc / "exe").resolve(strict=True)),
            "argv": (proc / "cmdline").read_bytes().decode().rstrip("\0").split("\0")}


def tty_facts(pid):
    proc = Path("/proc", str(pid), "fd")
    terminal = os.readlink(proc / "0")
    need(re.fullmatch(r"/dev/pts/\d+", terminal) and os.readlink(proc / "1") == terminal,
         "child stdin and stdout are outside its PTY")
    return {"path": terminal, "device": (proc / "0").stat().st_rdev, "inode": (proc / "0").stat().st_ino}


def socket_facts(path):
    info = path.lstat()
    need(stat.S_ISSOCK(info.st_mode) and info.st_uid == os.getuid(), "foreign session socket")
    return {"path": str(path), "inode": info.st_ino}


@contextmanager
def private_peer(path, expected_pid=None):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as peer:
        peer.settimeout(4)
        peer.connect(str(path))
        pid, uid, _ = struct.unpack("3i", peer.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        need(uid == os.getuid() and (expected_pid is None or expected_pid == pid), "foreign Unix peer")
        with peer.makefile("rwb") as stream:
            yield pid, stream


def rpc(stream, request):
    stream.write(json.dumps(request).encode() + b"\n")
    stream.flush()
    return json.loads(stream.readline(1024 * 1024))


def agent(path, app, method, params):
    with private_peer(path, app.pid) as (_, stream):
        result = rpc(stream, {"v": 1, "id": 1, "method": method, "params": params})
        need(result.get("id") == 1 and "error" not in result, "App observer refused: " + repr(result))
        return result["result"]


def windows(node):
    if node.get("type") == "con" and (node.get("shell") or node.get("window") is not None):
        yield node
    for child in node["nodes"] + node["floating_nodes"]:
        yield from windows(child)


def tree(env):
    return json.loads(command(["swaymsg", "-t", "get_tree", "-r"], env))


def title_snapshot(endpoint, app, pane):
    metadata = agent(endpoint, app, "readPaneMetadata", {"paneId": pane["paneId"]})
    need(metadata["paneId"] == pane["paneId"], "wrong App pane observed")
    return {"paneId": metadata["paneId"], "oscTitle": metadata.get("oscTitle"),
            "tabText": metadata["presentation"]["tabText"],
            "rows": metadata["cellGeometry"]["viewRows"], "cols": metadata["cellGeometry"]["viewCols"]}


class Run:
    def __init__(self, root, output, run_id, env):
        self.root = root
        self.output = output
        self.env = env
        self.run_id = run_id
        self.record = {"schemaVersion": 1, "runId": run_id, "coordinator": process_facts(os.getpid()),
                       "environment": {"uname": list(os.uname()[:1] + os.uname()[2:]),
                                       "packages": command(["dpkg-query", "-W", "sway", "xwayland", "foot", "libvte-2.91-0"], env)},
                       "foot": {"samples": {}}, "app": {"samples": {}}, "exits": {}}
        self.plan = read(SOURCE / "plan.json")

    @contextmanager
    def launch(self, name, args, env):
        with (self.output / (name + ".log")).open("wb") as log:
            proc = subprocess.Popen(args, env=env, cwd=self.root, stdin=subprocess.DEVNULL,
                                    stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                yield proc
            finally:
                if proc.poll() is None:
                    os.killpg(proc.pid, signal.SIGTERM)
                    try:
                        proc.wait(timeout=4)
                    except subprocess.TimeoutExpired:
                        os.killpg(proc.pid, signal.SIGKILL)
                        proc.wait(timeout=4)
                self.record["exits"][name] = {"pid": proc.pid, "returncode": proc.returncode}

    def desktop(self):
        config = self.root / "sway.conf"
        config.write_text("xwayland enable\noutput * resolution 1280x800\nseat seat0 hide_cursor 10000\n")
        self.record["swayConfig"] = config.read_text()
        runtime = Path(self.env["XDG_RUNTIME_DIR"])
        with self.launch("sway", ["/usr/bin/sway", "-c", str(config)], self.env) as sway:
            self.record["compositor"] = process_facts(sway.pid)
            sock = wait_for(lambda: single(runtime.glob("sway-ipc.*.sock")), "Sway IPC", sway)
            display = wait_for(lambda: single(p for p in runtime.glob("wayland-*") if p.is_socket()), "Wayland display", sway)
            x_socket = wait_for(lambda: single(Path("/tmp/.X11-unix").glob("X[0-9]*")), "Xwayland display", sway)
            self.env.update(SWAYSOCK=str(sock), WAYLAND_DISPLAY=display.name, DISPLAY=":" + x_socket.name[1:])
            with private_peer(sock, sway.pid), private_peer(display, sway.pid):
                self.record["sockets"] = [socket_facts(p) for p in (sock, display, x_socket)]
            self.run_foot()
            self.run_app()
            self.run_vte()
            need(not list(windows(tree(self.env))), "fixture windows remain in Sway")
        self.record["socketsGone"] = {str(p): not p.exists() for p in (sock, display, x_socket)}

    def run_foot(self):
        path = self.root / "foot.sock"
        args = self.child_args("foot", path)
        with self.launch("foot", ["/usr/bin/foot", "--config=/dev/null", "--app-id=" + BASE_ID, "--title=identity-foot", *args], self.env) as proc:
            self.terminal("foot", proc, path)
        self.after_terminal("foot")

    def child_args(self, name, path):
        return [PYTHON, "-I", "-B", str(SOURCE / "pty_child.py"), str(path), name + "-" + self.run_id, str(os.getpid())]

    def run_app(self):
        path = self.root / "app.sock"
        sidecar = self.root / "sidecar"
        sidecar.mkdir()
        subprocess.run(["tar", "-xf", "/inputs/sidecar.tar", "-C", sidecar], timeout=20, check=True)
        exe = sidecar / "Ntilde"
        self.record["app"]["files"] = self.app_hashes(sidecar)
        versions = set(re.findall(rb"(?<![0-9.])[0-9]+\.[0-9]+\.[0-9]+\+[a-f0-9]{40}(?![a-f0-9])",
                                  (sidecar / "Ntilde.dll").read_bytes()))
        need(len(versions) == 1, "missing unique App informational version")
        self.record["app"]["version"] = versions.pop().decode()
        data = self.root / "appdata"
        data.mkdir()
        args = self.child_args("app", path)
        settings = {"Profiles": [{"Id": "675f9248-1fb4-4092-b87b-4f1a62214012", "Name": "OSC identity QA",
                                  "Command": args[0], "Arguments": " ".join(args[1:]), "StartingDirectory": str(self.root), "Type": 0}],
                    "DefaultProfileId": "675f9248-1fb4-4092-b87b-4f1a62214012", "SessionPersistence": "Off",
                    "ShellExitPolicy": "Never", "PaneClosePolicy": "Always", "AutomaticUpdateChecks": False,
                    "CommandAssistEnabled": False, "CommandAssistShellIntegrationEnabled": False,
                    "QuakeModeEnabled": False, "AgentAccessObserveEnabled": True, "AgentAccessActEnabled": False}
        put(data / "settings.json", settings)
        self.record["app"]["settings"] = settings
        env = {**self.env, "NTILDE_APPDATA_ROOT": str(data)}
        with self.launch("app", [str(exe), "--ntilde-terminfo-owner=" + self.run_id], env) as proc:
            self.terminal("app", proc, path, data)
        self.after_terminal("app")
        need(self.record["app"]["files"] == self.app_hashes(sidecar), "App identity changed during execution")

    @staticmethod
    def app_hashes(directory):
        result = {}
        for path in sorted(directory.rglob("*")):
            info = path.lstat()
            need(info.st_uid == os.getuid() and not info.st_mode & 0o022
                 and (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)), "unexpected App tree entry")
            if path.is_file():
                result[str(path.relative_to(directory))] = digest(path)
        return result

    def terminal(self, name, proc, path, data=None):
        record = self.record[name]
        record["terminal"] = process_facts(proc.pid)
        wait_for(lambda: path if path.is_socket() else None, name + " raw PTY", proc)
        with private_peer(path) as (child, stream):
            record["child"] = process_facts(child)
            record["tty"] = tty_facts(child)
            need(record["child"]["parent"] == proc.pid and record["child"]["argv"] == self.child_args(name, path),
                 "wrong direct PTY child")
            await_window = lambda: single(w for w in windows(tree(self.env)) if w["pid"] == proc.pid)
            admission = wait_for(await_window, name + " owned Sway window", proc)
            window_id = admission["id"]
            record["admission"] = admission
            if name == "app":
                endpoint, pane = self.admit_app(record, proc, data)
            for phase, writes in self.plan[name].items():
                exchange = rpc(stream, writes)
                sample = {"exchange": exchange}
                record["samples"][phase] = sample
                need(record["child"] == process_facts(child) and record["tty"] == tty_facts(child), "PTY changed")
                if name == "foot":
                    expected = WANTED_ID if phase in ("pinned176", "request176") else BASE_ID
                    wait_for(lambda: self.find_window(window_id, proc.pid, expected), "foot app-ID " + phase, proc)
                sample["tree"] = tree(self.env)
                if name == "app":
                    self.observe_app(sample, proc, endpoint, pane, admission, phase)
            record["termios"] = rpc(stream, None)
        wait_for(lambda: True if not Path("/proc", str(child)).exists() else None, name + " PTY child exit")
        record["ptyGone"] = not Path(record["tty"]["path"]).exists()
        record["workerSocketGone"] = not path.exists()

    def find_window(self, window_id, pid, expected):
        return single(w for w in windows(tree(self.env)) if w["id"] == window_id and w["pid"] == pid and w["app_id"] == expected)

    def admit_app(self, record, proc, data):
        endpoint = data / "agent.sock"
        wait_for(lambda: endpoint if endpoint.is_socket() and (data / "agent-endpoint.json").exists() else None,
                 "App agent endpoint", proc)
        descriptor = read(data / "agent-endpoint.json")
        need(descriptor["pid"] == proc.pid and descriptor["endpoint"] == str(endpoint), "wrong App endpoint descriptor")
        pane = single(agent(endpoint, proc, "listSessions", {})["sessions"])
        screen = agent(endpoint, proc, "readScreen", {"paneId": pane["paneId"]})
        need(screen["lines"][1] == "app-" + self.run_id, "App pane did not display PTY marker")
        record.update(pane=pane, markerScreen=screen)
        return endpoint, pane

    def observe_app(self, sample, proc, endpoint, pane, admission, phase):
        window = admission.get("window")
        if window is not None:
            sample["xprop"] = command(["xprop", "-id", hex(window), "_NET_WM_PID", "WM_CLASS", "_NET_WM_NAME"], self.env)
        if phase.startswith("pinned"):
            sample["pane"] = title_snapshot(endpoint, proc, pane)
            return
        title = "identity-control" if phase == "control" else "identity-baseline"

        def title_readback():
            value = title_snapshot(endpoint, proc, pane)
            return value if value["oscTitle"] == title and title in value["tabText"] else None

        sample["pane"] = wait_for(title_readback, "App tab title " + phase, proc)

    def after_terminal(self, name):
        record = self.record[name]
        pid = record["terminal"]["pid"]
        wait_for(lambda: True if not any(w["pid"] == pid for w in windows(tree(self.env))) else None, name + " window removal")
        record["afterTree"] = tree(self.env)

    def run_vte(self):
        with self.launch("vte", [PYTHON, "-I", "-B", str(SOURCE / "vte_reference.py"), str(SOURCE / "plan.json")],
                         {**self.env, "GDK_BACKEND": "wayland"}) as proc:
            need(proc.wait(timeout=12) == 0, "VTE reference observer failed")
        self.record["vte"] = read(self.output / "vte.log")


def main(output, run_id):
    need(os.getuid() != 0 and re.fullmatch(r"[a-f0-9]{32}", run_id), "expected an unprivileged leased collector")
    output.mkdir(mode=0o700)
    root = Path(tempfile.mkdtemp(prefix="osc-"))
    home = root / "home"
    runtime = root / "runtime"
    for parent in (home, runtime):
        parent.mkdir(mode=0o700)
    env = {"HOME": str(home), "XDG_RUNTIME_DIR": str(runtime), "XDG_CONFIG_HOME": str(home / ".config"),
           "XDG_CACHE_HOME": str(home / ".cache"), "XDG_DATA_HOME": str(home / ".local/share"),
           "XDG_SESSION_TYPE": "wayland", "PATH": "/usr/bin:/bin", "LANG": "C.UTF-8",
           "WLR_BACKENDS": "headless", "WLR_RENDERER": "pixman", "WLR_LIBINPUT_NO_DEVICES": "1"}
    run = Run(root, output, run_id, env)
    try:
        run.desktop()
    except Exception as exc:
        run.record["error"] = str(exc)
        raise
    finally:
        shutil.rmtree(root)
        run.record["fixtureRemoved"] = not root.exists()
        put(output / "observation.json", run.record)


if __name__ == "__main__":
    main(Path(sys.argv[1]), sys.argv[2])
