"""Tagged private server controls; never an App or personal endpoint."""
import json
import os
import select
import socket
import stat
import sys
import time


def reply(value):
    print(json.dumps(value, separators=(",", ":")), flush=True)


class PrivateServer:
    def __init__(self, directory):
        metadata = os.lstat(directory)
        if not stat.S_ISDIR(metadata.st_mode) or stat.S_IMODE(metadata.st_mode) != 0o700 or metadata.st_uid != os.getuid():
            raise ValueError("Invalid private directory")
        self.endpoint = directory + "/agent.sock"
        self.descriptor = directory + "/agent-endpoint.json"
        self.listener = None
        self.connections = []
        self.accepted = self.closed = self.payload_bytes = self.commands = 0
        self.pending = None
        self.created_socket = self.created_descriptor = False

    def write_descriptor(self, pid):
        mode = "w" if self.created_descriptor else "x"
        with open(self.descriptor, mode, encoding="utf-8") as stream:
            self.created_descriptor = True
            json.dump({"version": 1, "pid": pid, "endpoint": self.endpoint}, stream)
        os.chmod(self.descriptor, 0o644)

    def mutate(self, kind):
        if kind == "socket-mode":
            os.chmod(self.endpoint, 0o666)
        elif kind == "descriptor-symlink":
            os.unlink(self.descriptor)
            os.symlink("synthetic-missing-descriptor", self.descriptor)
        elif kind == "close-listener":
            self.listener.close()
            self.listener = None
        elif kind == "socket-file":
            os.unlink(self.endpoint)
            with open(self.endpoint, "xb") as stream:
                stream.write(b"fixed-non-socket")
        else:
            raise ValueError("Unknown fixed fixture command")
        reply({"changed": kind})

    def control(self):
        line = sys.stdin.readline(128)
        if not line.endswith("\n") or self.commands >= 20:
            raise ValueError("Private fixture command bound")
        self.commands += 1
        command = json.loads(line)
        kind = command.get("kind")
        if kind == "exit":
            if self.pending is not None or self.connections:
                raise ValueError("Private fixture closed before connections settled")
            reply({"exiting": True, "accepted": self.accepted, "closed": self.closed, "payloadBytes": self.payload_bytes})
            return False
        if kind == "verify":
            count = command.get("connections")
            if type(count) is not int or not 0 <= count <= 4 or self.pending is not None:
                raise ValueError("Invalid connection count")
            self.pending = (count, time.monotonic() + 2)
        elif kind == "claim":
            pid = command.get("pid")
            if type(pid) is not int or not 2 <= pid <= 2147483647:
                raise ValueError("Invalid synthetic descriptor PID")
            self.write_descriptor(pid)
            reply({"changed": kind})
        else:
            self.mutate(kind)
        return True

    def settle(self):
        if self.pending is None:
            return
        count, expires = self.pending
        if self.accepted == count and not self.connections:
            reply({"accepted": self.accepted, "closed": self.closed, "payloadBytes": self.payload_bytes})
            self.pending = None
        elif time.monotonic() >= expires:
            raise RuntimeError("Private connection control did not settle")

    def poll(self):
        self.settle()
        watched = [sys.stdin, *self.connections]
        if self.listener is not None:
            watched.append(self.listener)
        readable, _, _ = select.select(watched, [], [], 0.2)
        for item in readable:
            if item is self.listener:
                connection, _ = self.listener.accept()
                connection.setblocking(False)
                self.connections.append(connection)
                self.accepted += 1
            elif item is sys.stdin:
                if not self.control():
                    return False
            else:
                data = item.recv(32)
                if data:
                    self.payload_bytes += len(data)
                    raise RuntimeError("Probe sent application bytes")
                item.close()
                self.connections.remove(item)
                self.closed += 1
        return True

    def close(self):
        for connection in self.connections:
            connection.close()
        if self.listener is not None:
            self.listener.close()
        for path, created in [(self.endpoint, self.created_socket), (self.descriptor, self.created_descriptor)]:
            if created:
                try:
                    os.unlink(path)  # Only this fixture's own entries; observer never unlinks.
                except FileNotFoundError:
                    pass

    def run(self):
        try:
            self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            self.listener.bind(self.endpoint)
            self.created_socket = True
            os.chmod(self.endpoint, 0o600)
            self.listener.listen(4)
            self.write_descriptor(os.getpid())
            reply({"ready": True, "pid": os.getpid()})
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if not self.poll():
                    return
            raise RuntimeError("Private fixture lifetime exceeded")
        finally:
            self.close()


def main():
    if sys.platform != "linux" or len(sys.argv) != 4:
        raise SystemExit(2)
    directory, run_id, marker = sys.argv[1:]
    if marker != "--ntilde-terminfo-owner=" + run_id or len(run_id) != 32 or any(char not in "0123456789abcdef" for char in run_id):
        raise SystemExit(2)
    PrivateServer(directory).run()


if __name__ == "__main__":
    main()
