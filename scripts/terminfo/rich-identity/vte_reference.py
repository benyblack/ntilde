"""GNOME VTE reference observer, driven through the widget's public feed API."""

import json
from pathlib import Path
import sys

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Vte", "2.91")
from gi.repository import GLib, Gtk, Vte


KEY = "vte.container.name"
Gtk.init([])
window = Gtk.Window(title="VTE OSC 666 control")
terminal = Vte.Terminal()
window.add(terminal)
window.show_all()
events = []
terminal.connect("termprops-changed", lambda widget, ids: events.extend(Vte.get_termprop_name(i) for i in ids))
samples = {}
phase = iter(json.loads(Path(sys.argv[1]).read_text())["vte"].items())


def sample(name, text):
    value = terminal.get_termprop_string(KEY)[0]
    samples[name] = {"feed": text.encode().hex(), "containerName": value, "changes": list(events)}
    events.clear()
    return advance()


def advance():
    item = next(phase, None)
    if item is None:
        Gtk.main_quit()
    else:
        name, text = item
        if text:
            terminal.feed(text.encode())
        GLib.timeout_add(230, sample, name, text)
    return GLib.SOURCE_REMOVE


GLib.idle_add(advance)
Gtk.main()
print(json.dumps({"version": [Vte.get_major_version(), Vte.get_minor_version(), Vte.get_micro_version()],
                  "registered": sorted(Vte.get_termprops()), "samples": samples}))
window.destroy()
