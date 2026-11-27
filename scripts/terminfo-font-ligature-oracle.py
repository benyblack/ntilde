"""Make independent HarfBuzz and outline references for Fira Code 6.2."""

import argparse
import hashlib
import json
from pathlib import Path

from fontTools.pens.pointInsidePen import PointInsidePen
from fontTools.ttLib import TTFont
import uharfbuzz as hb


ROOT = Path(__file__).resolve().parent.parent
FONT = ROOT / "tests/Ntilde.App.Tests/Fixtures/Fonts/FiraCode-Regular.ttf"
DIGEST = "5992ab9640e2df491b2f609467b1de60e8bc39b2c28db184342a0592d98f6117"
SIZE = 26
BOX = {"width": 32, "height": 32, "yFromBaseline": -24}


def shape(font, glyph_order, text, enabled):
    buffer = hb.Buffer()
    buffer.add_str(text)
    buffer.direction = "ltr"
    buffer.script = "latn"
    buffer.language = "en"
    features = {} if enabled else {"calt": False, "liga": False, "dlig": False}
    hb.shape(font, buffer, features)
    return [
        {"id": glyph.codepoint, "name": glyph_order[glyph.codepoint], "cluster": glyph.cluster,
         "advance": pos.x_advance, "xOffset": pos.x_offset, "yOffset": pos.y_offset}
        for glyph, pos in zip(buffer.glyph_infos, buffer.glyph_positions)
    ]


def sample(glyph_set, glyphs, upem, x, y):
    pen_x = 0
    unit = upem / SIZE
    for glyph in glyphs:
        point = (x * unit - pen_x - glyph["xOffset"], -y * unit - glyph["yOffset"])
        pen = PointInsidePen(glyph_set, point)
        glyph_set[glyph["name"]].draw(pen)
        if pen.getResult():
            return True
        pen_x += glyph["advance"]
    return False


def mask(glyph_set, glyphs, upem):
    rows = []
    for y in range(BOX["yFromBaseline"], BOX["yFromBaseline"] + BOX["height"]):
        row = []
        for x in range(BOX["width"]):
            coverage = sum(sample(glyph_set, glyphs, upem, x + dx, y + dy)
                           for dx in (0.25, 0.75) for dy in (0.25, 0.75))
            row.append(str(coverage))
        rows.append("".join(row))
    return rows


def report():
    data = FONT.read_bytes()
    if hashlib.sha256(data).hexdigest() != DIGEST:
        raise ValueError("Pinned Fira Code bytes changed")
    font = hb.Font(hb.Face(data))
    with TTFont(FONT) as table:
        glyphs = table.getGlyphOrder()
        glyph_set = table.getGlyphSet()
        names = table["name"]
        pairs = {}
        for text in ("=>", "!="):
            plain, linked = (shape(font, glyphs, text, enabled) for enabled in (False, True))
            if plain == linked or any(glyph["advance"] != 1200 for glyph in plain + linked):
                raise ValueError(f"Missing two-cell Fira Code substitution for {text}")
            pairs[text] = {"ordinary": plain, "shaped": linked,
                           "ordinaryMask": mask(glyph_set, plain, font.face.upem),
                           "shapedMask": mask(glyph_set, linked, font.face.upem)}
        return {"fontSha256": DIGEST, "family": names.getDebugName(1), "subfamily": names.getDebugName(2),
                "version": names.getDebugName(5), "postscript": names.getDebugName(6),
                "harfbuzz": hb.version_string(), "sizeDip": SIZE, "box": BOX, "fixtures": pairs}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    expected = json.dumps(report(), indent=2) + "\n"
    if args.check:
        if args.output.read_text(encoding="utf-8") != expected:
            parser.error("Retained shaping and outline oracle differs")
    else:
        args.output.write_text(expected, encoding="utf-8")


if __name__ == "__main__":
    main()
