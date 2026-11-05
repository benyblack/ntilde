"""Fixed private endpoint probe: credentials only, no application send/receive."""
import json
import os
import socket
import stat
import struct
import sys
import time

LIMIT = 4096
SOCKET_NAME = "agent.sock"
DESCRIPTOR_NAME = "agent-endpoint.json"


def stable(value):
    return {name: str(getattr(value, "st_" + name)) for name in ("dev", "ino", "uid", "mode")}


def regular(value):
    result = {name: str(getattr(value, "st_" + name)) for name in ("dev", "ino", "rdev", "uid", "mode", "size")}
    result.update({"mtimeNs": str(value.st_mtime_ns), "ctimeNs": str(value.st_ctime_ns)})
    return result


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate key")
        result[key] = value
    return result


def read_frame():
    data = bytearray()
    while True:
        chunk = os.read(0, min(1024, LIMIT + 1 - len(data)))
        if not chunk:
            break
        if len(chunk) > LIMIT - len(data):
            raise ValueError("Input byte limit")
        data.extend(chunk)
    if not data.endswith(b"\n") or data.count(b"\n") != 1:
        raise ValueError("Expected one frame")
    return json.loads(data.decode("utf-8"), object_pairs_hook=unique_object)


def validate_identity(value):
    if type(value) is not dict or set(value) != {"runId", "uid", "appPid", "appDataPath", "directory", "remainingMs"}:
        raise ValueError("Unexpected input fields")
    run_id = value["runId"]
    if type(run_id) is not str or len(run_id) != 32 or any(char not in "0123456789abcdef" for char in run_id):
        raise ValueError("Invalid run marker")
    if type(value["uid"]) is not int or value["uid"] != os.getuid() or type(value["appPid"]) is not int or not 2 <= value["appPid"] <= 2147483647:
        raise ValueError("Invalid expected credentials")
    if type(value["remainingMs"]) is not int or not 1 <= value["remainingMs"] <= 4000:
        raise ValueError("Invalid phase budget")


def validate_path(value):
    path = value["appDataPath"]
    if type(path) is not str or not path.startswith("/") or not path.endswith("/appdata") or len((path + "/" + SOCKET_NAME).encode("utf-8")) > 103:
        raise ValueError("Invalid endpoint path")
    if any(not part or part in (".", "..") for part in path.split("/")[1:]) or any(ord(char) < 32 or ord(char) == 127 for char in path):
        raise ValueError("Invalid canonical endpoint path")
    expected = value["directory"]
    if type(expected) is not dict or set(expected) != {"dev", "ino", "uid", "mode"} or any(type(item) is not str for item in expected.values()):
        raise ValueError("Invalid directory identity")


def input_record():
    value = read_frame()
    validate_identity(value)
    validate_path(value)
    return value


class Budget:
    def __init__(self, milliseconds):
        self.deadline = time.monotonic_ns() + milliseconds * 1000000

    def check(self):
        if time.monotonic_ns() >= self.deadline:
            raise TimeoutError("Endpoint phase expired")

    def query(self, action):
        self.check()
        value = action()
        self.check()
        return value

    def seconds(self):
        self.check()
        return (self.deadline - time.monotonic_ns()) / 1000000000


def directory_check(expected, budget):
    value = budget.query(lambda: os.fstat(3))
    if not stat.S_ISDIR(value.st_mode) or stat.S_IMODE(value.st_mode) != 0o700 or value.st_uid != expected["uid"] or stable(value) != expected["directory"]:
        raise ValueError("Held directory changed")
    return stable(value)


def socket_stat(expected, budget):
    value = budget.query(lambda: os.stat(SOCKET_NAME, dir_fd=3, follow_symlinks=False))
    if not stat.S_ISSOCK(value.st_mode) or stat.S_IMODE(value.st_mode) != 0o600 or value.st_uid != expected["uid"]:
        raise ValueError("Invalid private socket")
    return stable(value)


def descriptor_read(fd, expected, budget):
    before = budget.query(lambda: os.fstat(fd))
    if not stat.S_ISREG(before.st_mode) or before.st_uid != expected["uid"] or before.st_size > LIMIT:
        raise ValueError("Invalid descriptor file")
    data = bytearray()
    while True:
        chunk = budget.query(lambda: os.read(fd, min(1024, LIMIT + 1 - len(data))))
        if not chunk:
            break
        if len(chunk) > LIMIT - len(data):
            raise ValueError("Descriptor byte limit")
        data.extend(chunk)
    value = json.loads(data.decode("utf-8"), object_pairs_hook=unique_object)
    if type(value) is not dict or set(value) != {"version", "pid", "endpoint"} or type(value["version"]) is not int or value["version"] != 1:
        raise ValueError("Invalid descriptor schema")
    if type(value["pid"]) is not int or value["pid"] != expected["appPid"] or value["endpoint"] != expected["appDataPath"] + "/" + SOCKET_NAME:
        raise ValueError("Descriptor identity mismatch")
    return {"file": regular(before), **value}


def descriptor_check(fd, expected, original, budget):
    held = budget.query(lambda: os.fstat(fd))
    named = budget.query(lambda: os.stat(DESCRIPTOR_NAME, dir_fd=3, follow_symlinks=False))
    if not stat.S_ISREG(named.st_mode) or named.st_uid != expected["uid"] or regular(held) != original or regular(named) != original:
        raise ValueError("Descriptor changed")


def close_resources(connection, descriptor, result):
    for resource in ([connection] if connection is not None else []) + ([descriptor] if descriptor is not None else []) + [3, 4, 5]:
        try:
            if isinstance(resource, socket.socket):
                resource.close()
                if result["socket"] is not None:
                    result["socket"]["closed"] = True
            else:
                os.close(resource)
        except OSError as error:
            result["failures"].append({"code": "close", "errno": error.errno})


def execute():
    result = {"schemaVersion": 1, "runId": None, "ok": False, "failures": [], "directory": None,
              "descriptor": None, "socket": None, "inheritedClosed": False, "pythonVersion": list(sys.version_info[:3])}
    connection = None
    descriptor = None
    phase = "input"
    try:
        if sys.platform != "linux" or len(sys.argv) != 1:
            raise ValueError("Fixed actual-Linux probe only")
        expected = input_record()
        result["runId"] = expected["runId"]
        budget = Budget(expected["remainingMs"])
        phase = "directory"
        result["directory"] = directory_check(expected, budget)
        phase = "descriptor"
        budget.check()
        descriptor = os.open(DESCRIPTOR_NAME, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=3)
        budget.check()  # Record a returned fd before a post-open deadline can refuse it.
        result["descriptor"] = descriptor_read(descriptor, expected, budget)
        descriptor_check(descriptor, expected, result["descriptor"]["file"], budget)
        phase = "socket"
        named = socket_stat(expected, budget)
        connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        connection.settimeout(budget.seconds())
        budget.query(lambda: connection.connect("/proc/self/fd/3/" + SOCKET_NAME))
        kernel = stable(budget.query(lambda: os.fstat(connection.fileno())))
        phase = "credentials"
        peer_pid, peer_uid, peer_gid = struct.unpack("=iII", budget.query(lambda: connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)))
        result["socket"] = {"named": named, "kernel": kernel, "peer": {"pid": peer_pid, "uid": peer_uid, "gid": peer_gid}, "closed": False}
        if peer_pid != expected["appPid"] or peer_uid != expected["uid"]:
            raise ValueError("Kernel peer identity mismatch")
        phase = "socket"
        if socket_stat(expected, budget) != named:
            raise ValueError("Named socket changed")
        phase = "descriptor"
        descriptor_check(descriptor, expected, result["descriptor"]["file"], budget)
        phase = "directory"
        directory_check(expected, budget)
    except Exception as error:
        result["failures"].append({"code": "deadline" if isinstance(error, TimeoutError) else phase, "errno": getattr(error, "errno", None)})
    close_resources(connection, descriptor, result)
    result["inheritedClosed"] = not any(item["code"] == "close" for item in result["failures"])
    result["ok"] = not result["failures"]
    encoded = json.dumps(result, separators=(",", ":"), ensure_ascii=True).encode("ascii") + b"\n"
    if len(encoded) > LIMIT:
        raise RuntimeError("Fixed endpoint response exceeds its byte budget")
    sys.stdout.buffer.write(encoded)
    sys.stdout.buffer.flush()


if __name__ == "__main__":
    execute()
