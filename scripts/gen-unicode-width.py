#!/usr/bin/env python3
"""Generate the code point tables behind Ntilde.VT.UnicodeWidth.

A terminal has to agree with applications about how many cells a character takes, or the cursor
lands a column away from where the application believes it is and TUIs misdraw from there on.
Applications measure with wcwidth (glibc, Node, Rust's unicode-width, ...), and wcwidth's
double-width set is Unicode's East_Asian_Width property with value W (wide) or F (fullwidth),
UAX #11. The first table is exactly that set.

The second table is the text-default emoji: Emoji=Yes, Emoji_Presentation=No, East_Asian_Width
neither W nor F. They are one cell as text and become a two-cell emoji when VARIATION SELECTOR-16
asks for emoji presentation (U+2764 U+FE0F, U+00A9 U+FE0F, the "1 U+FE0F U+20E3" keycap). VS16 on
anything else is an unsupported sequence, which applications measure as the base plus a zero-width
selector, so it must not widen. The W/F text-default emoji (U+3030, U+303D, ...) are excluded
because they are two cells already.

Both used to be hand-written block spans. The width ranges missed BMP emoji that are EAW=W
(U+2705, U+26A1, U+2B50, ...) and made everything in U+1F000..U+1FBFF wide, and the VS16 check
covered U+2600..U+27BF only, which both widened non-emoji symbols there and missed text-default
emoji elsewhere (U+25B6, U+00A9, keycap digits). Deriving the tables from Unicode data removes the
guesswork, and regenerating them is how the terminal follows new Unicode versions.

Sources, with nothing downloaded:
  * East_Asian_Width: Python's own `unicodedata`, so its Unicode version is whatever the running
    interpreter ships (`unicodedata.unidata_version`, written into the output header). It applies
    UAX #11's defaults for unassigned code points, so the reserved parts of the CJK blocks and all
    of Planes 2 and 3 come out wide and future ideographs land correctly.
  * Emoji and Emoji_Presentation: the third-party `regex` module (`python -m pip install regex`),
    because `unicodedata` has no emoji properties. `regex` does not report its Unicode version, so
    the header records the package version instead.

Only the tables live here. Zero-width marks, controls and the grapheme-level rules (emoji
selectors, ZWJ sequences, flags, skin-tone modifiers) are hand-written in UnicodeWidth.cs and use
these lookups.

Usage: gen-unicode-width.py [--check]

  (no args)  Rewrite src/Ntilde.VT/UnicodeWidth.Tables.g.cs.
  --check    Write nothing; exit 1 if the committed file differs from what this interpreter and
             `regex` would generate.
"""

import sys
import unicodedata
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
OUTPUT = REPO_ROOT / "src" / "Ntilde.VT" / "UnicodeWidth.Tables.g.cs"
SCRIPT = "scripts/gen-unicode-width.py"
WIDE_CLASSES = ("W", "F")


def load_regex():
    try:
        import regex
    except ImportError:
        sys.exit(
            f"{SCRIPT} needs the third-party `regex` module for the Emoji and Emoji_Presentation "
            "properties, which Python's unicodedata does not provide.\n"
            "Install it with: python -m pip install regex"
        )
    return regex


def ranges_where(predicate):
    """Inclusive (start, end) ranges of every code point for which predicate(chr(cp)) holds."""
    ranges = []
    start = None
    for cp in range(0x110000 + 1):
        hit = cp <= 0x10FFFF and not (0xD800 <= cp <= 0xDFFF) and predicate(chr(cp))
        if hit and start is None:
            start = cp
        elif not hit and start is not None:
            ranges.append((start, cp - 1))
            start = None
    return ranges


def label(cp):
    ch = chr(cp)
    name = unicodedata.name(ch, "")
    if name:
        return name
    category = unicodedata.category(ch)
    return "<unassigned>" if category == "Cn" else f"<unnamed {category}>"


def render_table(name, summary, ranges):
    field = "_" + name[0].lower() + name[1:] + "Ranges"
    lines = [
        f"    /// <summary>Lowest code point in <see cref=\"{name}Ranges\"/>.</summary>",
        f"    internal const int {name}Min = 0x{ranges[0][0]:04X};",
        "",
        f"    /// <summary>Highest code point in <see cref=\"{name}Ranges\"/>.</summary>",
        f"    internal const int {name}Max = 0x{ranges[-1][1]:04X};",
        "",
        "    /// <summary>",
        *[f"    /// {line}" for line in summary],
        "    /// Inclusive (start, end) pairs, sorted ascending, non-overlapping and merged, so a binary",
        "    /// search over the pairs is exact.",
        "    /// </summary>",
        f"    internal static ReadOnlySpan<int> {name}Ranges => {field};",
        "",
        f"    private static readonly int[] {field} =",
        "    {",
    ]
    for start, end in ranges:
        comment = label(start) if start == end else f"{label(start)}..{label(end)}"
        lines.append(f"        0x{start:04X}, 0x{end:04X}, // {comment}")
    lines.append("    };")
    return lines


def render(tables, unicode_version, regex_version):
    lines = [
        "// <auto-generated>",
        f"//     Generated by {SCRIPT}. Do not edit by hand.",
        "//",
        f"//     East_Asian_Width: Unicode {unicode_version}, from Python's unicodedata module.",
        f"//     Emoji, Emoji_Presentation: the third-party `regex` module {regex_version}, which does",
        "//     not report its Unicode version (unicodedata has no emoji properties).",
        "//",
        "//     To regenerate, run from the repository root (`python -m pip install regex` first if it",
        "//     is missing):",
        f"//         python {SCRIPT}",
        f"//     and `python {SCRIPT} --check` verifies the committed file is current.",
        "// </auto-generated>",
        "",
        "namespace Ntilde.VT;",
        "",
        "public static partial class UnicodeWidth",
        "{",
        "    /// <summary>Unicode version of the East_Asian_Width data below.</summary>",
        f"    internal const string EastAsianWidthUnicodeVersion = \"{unicode_version}\";",
        "",
        "    // Each table is a static array exposed as a span, not `ReadOnlySpan<int> X => new int[] { ... }`.",
        "    // That form is free only where the JIT expands RuntimeHelpers.CreateSpan as an intrinsic;",
        "    // unoptimized (Debug) code calls the managed fallback, which allocates on every access - 72",
        "    // bytes a lookup, measured.",
    ]
    for name, summary, ranges in tables:
        lines.append("")
        lines += render_table(name, summary, ranges)
    lines += ["}", ""]
    return "\r\n".join(lines)


def main(argv):
    check = argv[1:] == ["--check"]
    if argv[1:] and not check:
        print(__doc__, file=sys.stderr)
        return 2

    regex = load_regex()
    emoji = regex.compile(r"\p{Emoji}")
    emoji_presentation = regex.compile(r"\p{Emoji_Presentation}")

    def is_wide(ch):
        return unicodedata.east_asian_width(ch) in WIDE_CLASSES

    def is_text_default_emoji(ch):
        return bool(emoji.match(ch)) and not emoji_presentation.match(ch) and not is_wide(ch)

    tables = [
        ("EastAsianWide",
         ["Code points whose East_Asian_Width is W or F: the double-width set wcwidth uses."],
         ranges_where(is_wide)),
        ("TextDefaultEmoji",
         ["Text-default emoji: Emoji=Yes, Emoji_Presentation=No, East_Asian_Width neither W nor F.",
          "One cell as text; VS16 makes them a two-cell emoji. VS16 on anything else is ignored."],
         ranges_where(is_text_default_emoji)),
    ]
    unicode_version = unicodedata.unidata_version
    text = render(tables, unicode_version, regex.__version__)
    shown = OUTPUT.relative_to(REPO_ROOT).as_posix()
    counts = ", ".join(f"{name} {len(ranges)} ranges" for name, _, ranges in tables)
    provenance = f"Unicode {unicode_version} + regex {regex.__version__}"

    if check:
        current = OUTPUT.read_bytes().decode("utf-8") if OUTPUT.exists() else ""
        # git may check the file out with either line ending, depending on the platform's config.
        if current.replace("\r\n", "\n") != text.replace("\r\n", "\n"):
            print(f"{shown} is out of date for {provenance}; run: python {SCRIPT}", file=sys.stderr)
            return 1
        print(f"{shown} is current for {provenance} ({counts}).")
        return 0

    # CRLF and no BOM, matching .editorconfig and the *.cs rule in .gitattributes.
    OUTPUT.write_bytes(text.encode("utf-8"))
    print(f"Wrote {shown}: {counts}, {provenance}.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
