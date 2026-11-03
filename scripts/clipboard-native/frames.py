"""Self-delimiting test payloads; native HGLOBAL allocations may contain padding."""
import struct
import zlib
import re


def frame_binary(payload):
    return struct.pack(">I", len(payload)) + payload


def binary_payload(data, allow_padding=False):
    if len(data) < 4:
        raise ValueError("Missing binary fixture length")
    length = struct.unpack_from(">I", data)[0]
    end = 4 + length
    if end > len(data) or (not allow_padding and end != len(data)):
        raise ValueError("Invalid binary fixture length")
    return data[4:end]


def html_fragment(data):
    offsets = {}
    boundary = 0
    for field in (b"StartHTML", b"EndHTML", b"StartFragment", b"EndFragment"):
        match = re.search(field + rb":[ \t]*(-?\d+)", data)
        if not match:
            raise ValueError("Missing native HTML offsets")
        offsets[field] = int(match[1].lstrip(b"0") or b"0")
        boundary = max(boundary, match.end())
    first, last = offsets[b"StartHTML"], offsets[b"EndHTML"]
    start, end = offsets[b"StartFragment"], offsets[b"EndFragment"]
    if (first, last) == (-1, -1):
        first, last = boundary, len(data)
    if not boundary <= first <= start <= end <= last <= len(data):
        raise ValueError("Invalid native HTML offsets")
    return data[start:end]


def _png_chunk(data, offset):
    length = struct.unpack_from(">I", data, offset)[0]
    kind = data[offset + 4:offset + 8]
    if offset + 12 + length > len(data):
        raise ValueError("Truncated native PNG chunk")
    payload = data[offset + 8:offset + 8 + length]
    checksum = struct.unpack_from(">I", data, offset + 8 + length)[0]
    if zlib.crc32(kind + payload) != checksum:
        raise ValueError("Native PNG chunk checksum differs")
    return kind, payload, offset + length + 12


def _png_channels(payload, position):
    if position != 8 or len(payload) != 13:
        raise ValueError("Invalid native PNG header length")
    width, height, depth, color, compression, filtering, interlace = struct.unpack(">IIBBBBB", payload)
    if (width, height, depth, compression, filtering, interlace) != (1, 1, 8, 0, 0, 0) or color not in (2, 6):
        raise ValueError("Unexpected native PNG fixture geometry/format")
    return 4 if color == 6 else 3


def _png_end(payload):
    if payload:
        raise ValueError("Invalid native PNG end chunk")


def png_pixel(data):
    """Validate a native PNG container and decode the fixture's single RGB(A) pixel."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Native PNG signature differs")
    offset, compressed, channels, ended = 8, [], None, False
    while offset + 12 <= len(data):
        previous = offset
        kind, payload, offset = _png_chunk(data, offset)
        if kind == b"IHDR":
            channels = _png_channels(payload, previous)
        elif kind == b"IDAT":
            compressed.append(payload)
        elif kind == b"IEND":
            _png_end(payload)
            ended = True
            break
    if not ended or channels is None:
        raise ValueError("Missing native PNG header/end")
    decoder = zlib.decompressobj()
    raw = decoder.decompress(b"".join(compressed), 64)
    if len(raw) != channels + 1 or raw[0] > 4 or not decoder.eof or decoder.unused_data:
        raise ValueError("Invalid native PNG pixel data")
    # Every first-pixel predictor is zero on the first/only row.
    return raw[1:] if channels == 4 else raw[1:] + b"\xff"
