"""An independent GTK4 clipboard peer for an isolated Xvfb desktop."""
import base64
import json
import os
import re
from pathlib import Path
import sys
import time
from frames import binary_payload

if os.environ.get("CI") != "true" or len(sys.argv) != 3:
    raise SystemExit("Requires an isolated CI desktop")

import gi
gi.require_version("Gdk", "4.0")
from gi.repository import Gdk, GLib

SELECTION_KEY = "NTILDE_CLIPBOARD_SELECTION"
root = Path(sys.argv[2])
loop = GLib.MainLoop()
reader = None


def receipt(name, data):
    provisional = root / (name + ".tmp")
    with provisional.open("x", encoding="utf-8") as output:
        json.dump(data, output)
    provisional.rename(root / (name + ".json"))


def text_transaction():
    """Expose an XID for XRes admission before accessing CLIPBOARD."""
    if os.environ.get(SELECTION_KEY) or os.environ.get("GDK_BACKEND") != "x11":
        raise RuntimeError("Requires the private X11 CLIPBOARD")
    gi.require_version("GdkX11", "4.0")
    from gi.repository import GdkX11

    display = Gdk.Display.open(os.environ["DISPLAY"])
    if not isinstance(display, GdkX11.X11Display):
        raise RuntimeError("GTK did not connect to X11")
    # This peer only needs a native XID for XRes. A GDK surface avoids
    # initializing a GTK widget renderer on the disposable display.
    surface = Gdk.Surface.new_toplevel(display)
    display.sync()
    receipt("ready", {"pid": os.getpid(), "window": surface.get_xid()})
    request = json.loads(sys.stdin.readline(1024))
    clipboard = display.get_clipboard()
    started = str(time.monotonic_ns())

    def complete(text):
        receipt("result", {"text": text, "started": started, "finished": str(time.monotonic_ns())})

    def read_done(source, result, *_):
        try:
            complete(source.read_text_finish(result))
        except Exception as error:
            receipt("result", {"error": str(error)})
            loop.quit()

    if request == {"kind": "read"}:
        clipboard.read_text_async(None, read_done)
    elif request.get("kind") == "seed" and re.fullmatch((r"(?:ntilde-(?:osc52-(?:baseline|seed)|paste-baseline)-[a-f0-9]{32}"
                       r"|terminfo-paste-[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})"), request.get("text", "")):
        text = request["text"]
        if not clipboard.set_content(Gdk.ContentProvider.new_for_value(text)):
            raise RuntimeError("Native CLIPBOARD seed refused")
        display.sync()
        complete(text)
    else:
        raise ValueError("Invalid isolated CLIPBOARD text request")
    loop.run()


if sys.argv[1] == "text":
    text_transaction()
    sys.exit(0)

gi.require_version("Gtk", "4.0")
from gi.repository import Gtk

Gtk.init()
display = Gdk.Display.get_default()
if display is None:
    raise RuntimeError("No isolated display")
clipboard = display.get_primary_clipboard() if os.environ.get(SELECTION_KEY) == "primary" else display.get_clipboard()
filename = "peer-fixture.json" if sys.argv[1] == "write" else "fixture.json"
values = {k: base64.b64decode(v) for k, v in json.loads((root / filename).read_text()).items()}


class ClipboardReader:
    def __init__(self):
        self.remaining = iter(values.items())
        self.failure = None
        self.chunks = []
        self.stream = None
        self.mime = None
        self.expected = None
        self.total = 0

    def next_value(self):
        try:
            self.mime, self.expected = next(self.remaining)
        except StopIteration:
            loop.quit()
            return
        self.chunks = []
        self.total = 0
        print("Native GTK read start:", self.mime, flush=True)
        clipboard.read_async([self.mime], GLib.PRIORITY_DEFAULT, None, self.opened)

    def opened(self, source, result, *_):
        try:
            self.stream, actual_mime = source.read_finish(result)
            if actual_mime != self.mime:
                raise RuntimeError("Unexpected native MIME: " + self.mime)
            # GTK 4.22's INCR flush clears pending_task after completing its task.
            # Queue the next read after that cleanup, rather than replacing it reentrantly.
            GLib.idle_add(self.read_chunk)
        except Exception as error:
            self.fail(error)

    def read_chunk(self):
        self.stream.read_bytes_async(4096, GLib.PRIORITY_DEFAULT, None, self.received)

    def received(self, input_stream, outcome, *_):
        try:
            block = input_stream.read_bytes_finish(outcome).get_data()
            if block:
                self.chunks.append(block)
                self.total += len(block)
                if self.total % 65536 == 0:
                    print("Native GTK read progress:", self.mime, self.total, flush=True)
                GLib.idle_add(self.read_chunk)
                return
            input_stream.close(None)
            actual = b"".join(self.chunks)
            if self.mime.startswith("application/x-ntilde-qa"):
                actual = binary_payload(actual)
                expected = binary_payload(self.expected)
            else:
                expected = self.expected
            if actual != expected:
                raise RuntimeError("Native GTK clipboard mismatch: " + self.mime)
            print("Native GTK read complete:", self.mime, len(actual), flush=True)
            GLib.idle_add(self.next_value)
        except Exception as error:
            self.fail(error)

    def fail(self, error):
        self.failure = error
        loop.quit()


def publish(data):
    providers = [Gdk.ContentProvider.new_for_bytes(mime, GLib.Bytes.new(value)) for mime, value in data.items()]
    if os.environ.get(SELECTION_KEY) == "primary":
        # Exercise a TARGETS list beyond the old 144-atom rejection.
        providers += [Gdk.ContentProvider.new_for_bytes("application/x-ntilde-extra-" + str(i), GLib.Bytes.new(b"owned target fixture")) for i in range(200)]
    if not clipboard.set_content(Gdk.ContentProvider.new_union(providers)):
        raise RuntimeError("Native clipboard write refused")
    display.sync()


def republish_when_requested():
    if not (root / "peer-republish.request").exists():
        return True
    replacement = {k: base64.b64decode(v) for k, v in json.loads((root / "fixture.json").read_text()).items()}
    publish(replacement)
    (root / "peer-republish.ready").write_text("ok")
    return False


if sys.argv[1] == "write":
    publish(values)
    if os.environ.get(SELECTION_KEY) == "primary":
        GLib.timeout_add(10, republish_when_requested)
    (root / "peer-write.ready").write_text("ok")
elif sys.argv[1] == "read":
    reader = ClipboardReader()
    reader.next_value()
else:
    raise SystemExit(2)
loop.run()
if reader and reader.failure:
    raise reader.failure
