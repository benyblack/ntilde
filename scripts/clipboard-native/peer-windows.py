"""Independent Win32 clipboard peer on an isolated Windows CI desktop."""
import base64
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import sys
import time
from frames import binary_payload, png_pixel, html_fragment

if os.environ.get("CI") != "true" or len(sys.argv) != 3:
    raise SystemExit("Requires an isolated CI desktop")

TEXT = "text/plain"
HTML = "text/html"

user = ctypes.WinDLL("user32", use_last_error=True)
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
user.OpenClipboard.argtypes = [wintypes.HWND]
user.OpenClipboard.restype = wintypes.BOOL
user.CloseClipboard.restype = wintypes.BOOL
user.EmptyClipboard.restype = wintypes.BOOL
user.RegisterClipboardFormatW.argtypes = [wintypes.LPCWSTR]
user.RegisterClipboardFormatW.restype = wintypes.UINT
user.GetClipboardData.argtypes = [wintypes.UINT]
user.GetClipboardData.restype = wintypes.HANDLE
user.SetClipboardData.argtypes = [wintypes.UINT, wintypes.HANDLE]
user.SetClipboardData.restype = wintypes.HANDLE
kernel.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
kernel.GlobalAlloc.restype = wintypes.HGLOBAL
kernel.GlobalLock.argtypes = [wintypes.HGLOBAL]
kernel.GlobalLock.restype = ctypes.c_void_p
kernel.GlobalUnlock.argtypes = [wintypes.HGLOBAL]
kernel.GlobalSize.argtypes = [wintypes.HGLOBAL]
kernel.GlobalSize.restype = ctypes.c_size_t
kernel.GlobalFree.argtypes = [wintypes.HGLOBAL]
kernel.GetConsoleWindow.restype = wintypes.HWND
user.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
                                ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, ctypes.c_void_p]
user.CreateWindowExW.restype = wintypes.HWND
user.DestroyWindow.argtypes = [wintypes.HWND]


def html_format(fragment):
    prefix = b"<html><body><!--StartFragment-->"
    suffix = b"<!--EndFragment--></body></html>"
    template = "Version:1.0\r\nStartHTML:{:010d}\r\nEndHTML:{:010d}\r\nStartFragment:{:010d}\r\nEndFragment:{:010d}\r\n"
    start = len(template.format(0, 0, 0, 0).encode())
    header = template.format(start, start + len(prefix + fragment + suffix), start + len(prefix), start + len(prefix + fragment)).encode()
    return header + prefix + fragment + suffix


def format_id(mime):
    if mime == TEXT:
        return 13  # CF_UNICODETEXT: the standard Win32 text representation.
    return user.RegisterClipboardFormatW({"image/png": "PNG", HTML: "HTML Format"}.get(mime, mime))


def fetch(format_number):
    handle = user.GetClipboardData(format_number)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    address = kernel.GlobalLock(handle)
    if not address:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        length = kernel.GlobalSize(handle)
        if length > 1024 * 1024:
            raise RuntimeError("Unexpected clipboard fixture size")
        return ctypes.string_at(address, length)
    finally:
        kernel.GlobalUnlock(handle)


def put(format_number, value):
    handle = kernel.GlobalAlloc(0x0042, len(value))  # GMEM_MOVEABLE | GMEM_ZEROINIT
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        address = kernel.GlobalLock(handle)
        if not address:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            ctypes.memmove(address, value, len(value))
        finally:
            kernel.GlobalUnlock(handle)
        if not user.SetClipboardData(format_number, handle):
            raise ctypes.WinError(ctypes.get_last_error())
        handle = None  # ownership transferred to Windows
    finally:
        if handle:
            kernel.GlobalFree(handle)


root = Path(sys.argv[2])
filename = "peer-fixture.json" if sys.argv[1] == "write" else "fixture.json"
values = {k: base64.b64decode(v) for k, v in json.loads((root / filename).read_text()).items()}
deadline = time.monotonic() + 5
owner = user.CreateWindowExW(0, "STATIC", "Ntilde clipboard QA", 0, 0, 0, 0, 0, -3, None, None, None)
if not owner:
    raise ctypes.WinError(ctypes.get_last_error())
while not user.OpenClipboard(owner):
    if time.monotonic() >= deadline:
        raise ctypes.WinError(ctypes.get_last_error())
    time.sleep(0.02)
try:
    if sys.argv[1] == "read":
        for mime, expected in values.items():
            try:
                actual = fetch(format_id(mime))
            except OSError as error:
                raise RuntimeError("Native clipboard format unavailable: " + mime) from error
            if mime == TEXT:
                text, terminator, _ = actual.decode("utf-16-le").partition("\0")
                if not terminator:
                    raise RuntimeError("Native Unicode text lacks a terminating NUL")
                actual = text.encode()
            elif mime == HTML:
                actual = html_fragment(actual)
            if mime == "image/png":
                if png_pixel(actual) != png_pixel(expected):
                    raise RuntimeError("Native Win32 clipboard PNG pixels differ")
            elif mime.startswith("application/x-ntilde-qa"):
                if binary_payload(actual, allow_padding=True) != binary_payload(expected):
                    raise RuntimeError("Native Win32 binary clipboard mismatch: " + mime)
            elif actual != expected:
                raise RuntimeError("Native Win32 clipboard mismatch: " + mime)
    elif sys.argv[1] == "write":
        if not user.EmptyClipboard():
            raise ctypes.WinError(ctypes.get_last_error())
        for mime, value in values.items():
            if mime == TEXT:
                value = value.decode().encode("utf-16-le") + b"\0\0"
            elif mime == HTML:
                value = html_format(value)
            put(format_id(mime), value)
    else:
        raise SystemExit(2)
finally:
    user.CloseClipboard()
if sys.argv[1] == "write":
    (root / "peer-write.ready").write_text("ok")
    while True:
        time.sleep(1)
user.DestroyWindow(owner)
