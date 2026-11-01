"""An independent GTK4 clipboard peer for an isolated Xvfb desktop."""
import base64
import json
import os
from pathlib import Path
import sys
from frames import binary_payload

if os.environ.get("CI") != "true" or len(sys.argv) != 3:
    raise SystemExit("Requires an isolated CI desktop")

import gi
gi.require_version("Gdk", "4.0")
gi.require_version("Gtk", "4.0")
from gi.repository import Gdk, GLib, Gtk

root = Path(sys.argv[2])
filename = "peer-fixture.json" if sys.argv[1] == "write" else "fixture.json"
values = {k: base64.b64decode(v) for k, v in json.loads((root / filename).read_text()).items()}
Gtk.init()
display = Gdk.Display.get_default()
if display is None:
    raise RuntimeError("No isolated display")
clipboard = display.get_clipboard()
loop = GLib.MainLoop()
reader = None


class ClipboardReader:
    def __init__(self):
        self.remaining = iter(values.items())
        self.failure = None
        self.chunks = []
        self.stream = None
        self.mime = None
        self.expected = None

    def next_value(self):
        try:
            self.mime, self.expected = next(self.remaining)
        except StopIteration:
            loop.quit()
            return
        self.chunks = []
        clipboard.read_async([self.mime], GLib.PRIORITY_DEFAULT, None, self.opened)

    def opened(self, source, result, *_):
        try:
            self.stream, actual_mime = source.read_finish(result)
            if actual_mime != self.mime:
                raise RuntimeError("Unexpected native MIME: " + self.mime)
            self.read_chunk()
        except Exception as error:
            self.fail(error)

    def read_chunk(self):
        self.stream.read_bytes_async(4096, GLib.PRIORITY_DEFAULT, None, self.received)

    def received(self, input_stream, outcome, *_):
        try:
            block = input_stream.read_bytes_finish(outcome).get_data()
            if block:
                self.chunks.append(block)
                self.read_chunk()
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
            self.next_value()
        except Exception as error:
            self.fail(error)

    def fail(self, error):
        self.failure = error
        loop.quit()


if sys.argv[1] == "write":
    providers = [Gdk.ContentProvider.new_for_bytes(mime, GLib.Bytes.new(data)) for mime, data in values.items()]
    if not clipboard.set_content(Gdk.ContentProvider.new_union(providers)):
        raise RuntimeError("Native clipboard write refused")
    display.sync()
    (root / "peer-write.ready").write_text("ok")
elif sys.argv[1] == "read":
    reader = ClipboardReader()
    reader.next_value()
else:
    raise SystemExit(2)
loop.run()
if reader and reader.failure:
    raise reader.failure
