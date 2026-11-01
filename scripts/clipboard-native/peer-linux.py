"""An independent GTK4 clipboard peer for an isolated Xvfb desktop."""
import base64
import json
import os
from pathlib import Path
import sys

if os.environ.get("CI") != "true" or len(sys.argv) != 3:
    raise SystemExit("Requires an isolated CI desktop")

import gi
gi.require_version("Gdk", "4.0")
from gi.repository import Gdk, GLib

root = Path(sys.argv[2])
filename = "peer-fixture.json" if sys.argv[1] == "write" else "fixture.json"
values = {k: base64.b64decode(v) for k, v in json.loads((root / filename).read_text()).items()}
display = Gdk.Display.open(os.environ["DISPLAY"])
if display is None:
    raise RuntimeError("No isolated display")
clipboard = display.get_clipboard()
loop = GLib.MainLoop()
failure = None


def read_all():
    remaining = iter(values.items())
    def next_value():
        try:
            mime, expected = next(remaining)
        except StopIteration:
            loop.quit()
            return
        def opened(source, result):
            global failure
            try:
                stream, actual_mime = source.read_finish(result)
                if actual_mime != mime:
                    raise RuntimeError("Unexpected native MIME: " + mime)
                chunks = []
                def received(input_stream, outcome):
                    global failure
                    try:
                        block = input_stream.read_bytes_finish(outcome).get_data()
                        if block:
                            chunks.append(block)
                            input_stream.read_bytes_async(4096, GLib.PRIORITY_DEFAULT, None, received)
                        else:
                            input_stream.close(None)
                            if b"".join(chunks) != expected:
                                raise RuntimeError("Native GTK clipboard mismatch: " + mime)
                            next_value()
                    except Exception as error:
                        failure = error
                        loop.quit()
                stream.read_bytes_async(4096, GLib.PRIORITY_DEFAULT, None, received)
            except Exception as error:
                failure = error
                loop.quit()
        clipboard.read_async([mime], GLib.PRIORITY_DEFAULT, None, opened)
    next_value()


if sys.argv[1] == "write":
    providers = [Gdk.ContentProvider.new_for_bytes(mime, GLib.Bytes.new(data)) for mime, data in values.items()]
    if not clipboard.set_content(Gdk.ContentProvider.new_union(providers)):
        raise RuntimeError("Native clipboard write refused")
    display.sync()
    (root / "peer-write.ready").write_text("ok")
elif sys.argv[1] == "read":
    read_all()
else:
    raise SystemExit(2)
loop.run()
if failure:
    raise failure
