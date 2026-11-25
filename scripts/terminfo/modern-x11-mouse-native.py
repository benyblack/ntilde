"""Fixed Linux X11 and peer-credential operations for one owned display."""

import ctypes as ct
import json
import os
import signal
import socket
import struct
import sys
import time

XID, INT, PTR = ct.c_ulong, ct.c_int, ct.c_void_p
INTP, XIDP = ct.POINTER(INT), ct.POINTER(XID)
UINT, UINTP = ct.c_uint, ct.POINTER(ct.c_uint)


class WindowAttributes(ct.Structure):
    _fields_ = [
        ("x", INT), ("y", INT), ("width", INT), ("height", INT), ("border_width", INT),
        ("depth", INT), ("visual", PTR), ("root", XID), ("window_class", INT),
        ("bit_gravity", INT), ("win_gravity", INT), ("backing_store", INT),
        ("backing_planes", XID), ("backing_pixel", XID), ("save_under", INT),
        ("colormap", XID), ("map_installed", INT), ("map_state", INT),
        ("all_event_masks", ct.c_long), ("your_event_mask", ct.c_long),
        ("do_not_propagate_mask", ct.c_long), ("override_redirect", INT), ("screen", PTR),
    ]


class ClientSpec(ct.Structure):
    _fields_ = [("client", XID), ("mask", UINT)]


class ClientId(ct.Structure):
    _fields_ = [("spec", ClientSpec), ("length", ct.c_long), ("value", PTR)]


def bind(lib, name, result, *args):
    call = getattr(lib, name)
    call.restype, call.argtypes = result, args
    return call


def demand(value, reason):
    if not value:
        raise ValueError(reason)


def peer(socket_handle, expected):
    result = struct.unpack("3i", socket_handle.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
    demand(result[0] == expected and result[1] == os.getuid(), "wrong UNIX socket peer")
    return result[0]


def request_peer(path, pid, request):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as conn:
        conn.settimeout(4)
        conn.connect(path)
        peer(conn, pid)
        conn.sendall(json.dumps(request).encode() + b"\n")
        with conn.makefile("rb") as stream:
            response = stream.readline(262145)
        demand(response.endswith(b"\n") and len(response) <= 262144, "invalid bounded peer response")
        return json.loads(response)


class XDisplay:
    def __init__(self, server_pid):
        x11 = ct.CDLL("libX11.so.6")
        xtst = ct.CDLL("libXtst.so.6")
        xres = ct.CDLL("libXRes.so.1")
        self.close = bind(x11, "XCloseDisplay", INT, PTR)
        self.free = bind(x11, "XFree", INT, PTR)
        self.query_tree = bind(x11, "XQueryTree", INT, PTR, XID, XIDP, XIDP, ct.POINTER(XIDP), UINTP)
        self.attributes = bind(x11, "XGetWindowAttributes", INT, PTR, XID, ct.POINTER(WindowAttributes))
        self.get_focus = bind(x11, "XGetInputFocus", INT, PTR, XIDP, INTP)
        self.set_focus = bind(x11, "XSetInputFocus", INT, PTR, XID, INT, XID)
        self.query_pointer = bind(x11, "XQueryPointer", INT, PTR, XID, XIDP, XIDP, INTP, INTP, INTP, INTP, UINTP)
        self.sync = bind(x11, "XSync", INT, PTR, INT)
        self.move = bind(xtst, "XTestFakeMotionEvent", INT, PTR, INT, INT, INT, XID)
        self.button = bind(xtst, "XTestFakeButtonEvent", INT, PTR, UINT, INT, XID)
        self.key = bind(xtst, "XTestFakeKeyEvent", INT, PTR, UINT, INT, XID)
        self.keymap = bind(x11, "XQueryKeymap", INT, PTR, ct.POINTER(ct.c_ubyte))
        self.keysym = bind(x11, "XStringToKeysym", XID, ct.c_char_p)
        self.keycode = bind(x11, "XKeysymToKeycode", ct.c_ubyte, PTR, XID)
        self.keyboard_mapping = bind(x11, "XGetKeyboardMapping", XIDP, PTR, ct.c_ubyte, INT, INTP)
        self.keysym_modifiers = bind(x11, "XkbKeysymToModifiers", UINT, PTR, XID)
        self.lookup_keysym = bind(x11, "XkbLookupKeySym", INT, PTR, ct.c_ubyte, UINT, UINTP, XIDP)
        self.query_ids = bind(xres, "XResQueryClientIds", INT, PTR, ct.c_long,
                              ct.POINTER(ClientSpec), ct.POINTER(ct.c_long), ct.POINTER(ct.POINTER(ClientId)))
        self.get_pid = bind(xres, "XResGetClientPid", INT, ct.POINTER(ClientId))
        self.destroy_ids = bind(xres, "XResClientIdsDestroy", None, ct.c_long, ct.POINTER(ClientId))
        self.display = bind(x11, "XOpenDisplay", PTR, ct.c_char_p)(None)
        demand(self.display, "owned X display unavailable")
        try:
            fd = bind(x11, "XConnectionNumber", INT, PTR)(self.display)
            with socket.socket(fileno=os.dup(fd)) as conn:
                self.server_pid = peer(conn, server_pid)
            test = bind(xtst, "XTestQueryExtension", INT, PTR, INTP, INTP, INTP, INTP)
            version = [INT() for _ in range(4)]
            demand(test(self.display, *(ct.byref(value) for value in version)), "XTEST is unavailable")
            self.version = [version[2].value, version[3].value]
            self.root = bind(x11, "XDefaultRootWindow", XID, PTR)(self.display)
        except Exception:
            self.close(self.display)
            raise

    def client_pid(self, window):
        spec, count, ids = ClientSpec(window, 2), ct.c_long(), ct.POINTER(ClientId)()
        status = self.query_ids(self.display, 1, ct.byref(spec), ct.byref(count), ct.byref(ids))
        try:
            demand(status == 0 and count.value == 1 and bool(ids), "missing XRes window client")
            return self.get_pid(ids)
        finally:
            if ids:
                self.destroy_ids(count.value, ids)

    def window(self, app_pid):
        root, parent, children, count = XID(), XID(), XIDP(), UINT()
        demand(self.query_tree(self.display, self.root, ct.byref(root), ct.byref(parent),
                               ct.byref(children), ct.byref(count)), "cannot enumerate owned display root")
        try:
            demand(count.value <= 32, "unexpected owned display window count")
            visible = []
            for index in range(count.value):
                window, info = children[index], WindowAttributes()
                demand(self.attributes(self.display, window, ct.byref(info)), "missing owned X11 geometry")
                if info.map_state == 2 and info.width >= 400 and info.height >= 300:
                    visible.append({"id": window, "x": info.x, "y": info.y, "width": info.width, "height": info.height})
            demand(len(visible) == 1, "expected a single mapped App window")
            demand(self.client_pid(visible[0]["id"]) == app_pid, "wrong mapped X window owner")
            return visible[0]
        finally:
            if children:
                self.free(children)

    def focus(self):
        window, revert = XID(), INT()
        self.get_focus(self.display, ct.byref(window), ct.byref(revert))
        return window.value

    def pointer(self):
        root, child, x, y, wx, wy, mask = XID(), XID(), INT(), INT(), INT(), INT(), UINT()
        demand(self.query_pointer(self.display, self.root, ct.byref(root), ct.byref(child), ct.byref(x),
                                  ct.byref(y), ct.byref(wx), ct.byref(wy), ct.byref(mask)), "cannot read XTEST pointer")
        return {"window": child.value, "point": [x.value, y.value], "mask": mask.value}

    def type_fence(self, tag):
        demand(len(tag) == 2 and tag[0] == "z" and "a" <= tag[1] <= "s", "invalid XTEST fence")
        codes = [self.keycode(self.display, self.keysym(letter.encode("ascii"))) for letter in tag]
        demand(all(codes), "XTEST fence key mapping unavailable")
        started = str(time.monotonic_ns())
        for code in codes:
            demand(self.key(self.display, code, 1, 0) and self.key(self.display, code, 0, 0),
                   "XTEST fence dispatch failed")
        self.sync(self.display, 0)
        return {"tag": tag, "started": started, "keycodes": codes}

    def pressed(self):
        keys = (ct.c_ubyte * 32)()
        self.keymap(self.display, keys)
        return [code for code in range(8, 256) if keys[code // 8] & (1 << (code % 8))]

    def effective_keysym(self, code, state):
        adjusted_modifiers, result = UINT(), XID()
        demand(self.lookup_keysym(self.display, code, state, ct.byref(adjusted_modifiers), ct.byref(result)),
               "cannot translate the KP_Add keycode")
        return result.value

    def keypad_mapping(self):
        name = "KP_Add"
        keysym = self.keysym(name.encode("ascii"))
        code = self.keycode(self.display, keysym)
        demand(code >= 8, "KP_Add keycode unavailable")
        count = INT()
        entries = self.keyboard_mapping(self.display, code, 1, ct.byref(count))
        try:
            demand(entries and 1 <= count.value <= 32, "KP_Add symbol levels unavailable")
            levels = entries[:count.value]
            num_lock_mask = self.keysym_modifiers(self.display, self.keysym(b"Num_Lock"))
            unmodified = self.effective_keysym(code, 0)
            num_locked = self.effective_keysym(code, num_lock_mask)
            demand(0 < num_lock_mask <= 255 and unmodified == keysym == num_locked and keysym in levels,
                   f"KP_Add translation differs under NumLock: {levels}, {num_lock_mask}, {unmodified}, {num_locked}")
            return {"name": name, "keysym": keysym, "code": code, "levels": levels,
                    "numLockMask": num_lock_mask, "unmodifiedKeysym": unmodified, "numLockKeysym": num_locked}
        finally:
            if entries:
                self.free(entries)

    def chord(self, names, window_id, mapping=None):
        held, events = [], []
        if mapping is None:
            mapping = {name: self.keycode(self.display, self.keysym(name.encode("ascii"))) for name in names}
        demand(all(mapping.values()) and len(set(mapping.values())) == len(names), "bad chord key mapping")
        try:
            for down, order in ((True, names), (False, reversed(names))):
                for name in order:
                    code = mapping[name]
                    demand(self.focus() == window_id, "XTEST keyboard focus changed")
                    at = str(time.monotonic_ns())
                    if down:
                        held.append(code)
                    demand(self.key(self.display, code, down, 0), "XTEST key dispatch failed")
                    self.sync(self.display, 0)
                    if not down:
                        held.remove(code)
                    pressed, mask = self.pressed(), self.pointer()["mask"]
                    demand(pressed == sorted(held), "XTEST held keys differ")
                    events.append({"name": name, "code": code, "down": down, "at": at,
                                   "pressed": pressed, "mask": mask})
        finally:
            for code in reversed(held):
                self.key(self.display, code, 0, 0)
            self.sync(self.display, 0)
            if held:
                demand(not self.pressed(), "XTEST key cleanup failed")
        return events

    def keyboard_action(self, command, window_id):
        request, tag = command["request"], command["fence"]
        permitted = {"a": (["a"], ("zb",)), "ctrl+i": (["Control_L", "i"], ("zc",)),
                     "KP_Add": (["KP_Add"], ("zd", "ze"))}
        names, tags = permitted[request["chord"]]
        demand(tag in tags and self.pressed() == [] and self.pointer()["mask"] == 0,
               "unexpected keyboard fence or initial state")
        started = str(time.monotonic_ns())
        mapping = self.keypad_mapping() if names == ["KP_Add"] else None
        events = self.chord(names, window_id, {mapping["name"]: mapping["code"]} if mapping else None)
        fence_start = str(time.monotonic_ns())
        fence_events = [event for letter in tag for event in self.chord([letter], window_id)]
        focus, pressed, mask = self.focus(), self.pressed(), self.pointer()["mask"]
        demand(focus == window_id and not pressed and mask == 0, "XTEST keyboard restoration failed")
        return {"request": request, "started": started, "events": events, "initialKeys": [],
                **({"mapping": mapping} if mapping else {}),
                "fence": {"tag": tag, "started": fence_start, "events": fence_events,
                          "keycodes": [event["code"] for event in fence_events if event["down"]]},
                "finished": str(time.monotonic_ns()), "serverPid": self.server_pid, "appPid": command["appPid"],
                "windowId": window_id, "focus": focus, "finalKeys": pressed, "finalMask": mask, "xtest": True}

    def run(self, command):
        app_pid = command["appPid"]
        window = self.window(app_pid)
        window_id = window["id"]
        if command["kind"] == "admit":
            self.set_focus(self.display, window_id, 1, 0)
            self.sync(self.display, 0)
            demand(self.focus() == window_id, "owned window did not receive focus")
            return {"window": window, "serverPid": self.server_pid, "appPid": app_pid,
                    "focus": window_id, "xtestVersion": self.version}
        demand(window == command["window"] and self.focus() == window_id, "X window geometry or focus changed")
        request = command["request"]
        if request["kind"] == "keyboard":
            return self.keyboard_action(command, window_id)
        before = self.pointer()
        x, y = request["point"]
        demand(window["x"] < x < window["x"] + window["width"]
               and window["y"] < y < window["y"] + window["height"], "point outside owned X window")
        demand(before["mask"] == request["beforeMask"], "unexpected X11 button/modifier state")
        started = str(time.monotonic_ns())
        if request["kind"] == "move" and request["button"] == 0:
            sent = self.move(self.display, 0, x, y, 0)
        else:
            demand(request["kind"] in ("press", "release") and request["button"] in (1, 4)
                   and before["point"] == [x, y] and before["window"] == window_id, "invalid XTEST button target")
            sent = self.button(self.display, request["button"], request["kind"] == "press", 0)
        demand(sent, "XTEST request failed")
        self.sync(self.display, 0)
        after = self.pointer()
        demand(after == {"window": window_id, "point": [x, y], "mask": request["afterMask"]}
               and self.focus() == window_id, "XTEST action or post-action focus did not read back")
        fence = self.type_fence(command["fence"])
        demand(self.focus() == window_id, "XTEST fence focus changed")
        return {"request": request, "before": before, "after": after, "started": started, "fence": fence,
                "finished": str(time.monotonic_ns()), "serverPid": self.server_pid,
                "appPid": app_pid, "windowId": window_id, "focus": window_id, "xtest": True}


def interrupt(_signum, _frame):
    raise InterruptedError("XTEST action interrupted")


def main():
    command = json.loads(sys.argv[1])
    if command["kind"] == "rpc":
        result = request_peer(command["socket"], command["pid"], command["request"])
    else:
        if command.get("request", {}).get("kind") == "keyboard":
            for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
                signal.signal(signum, interrupt)
        display = XDisplay(command["serverPid"])
        try:
            result = display.run(command)
        finally:
            display.close(display.display)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
