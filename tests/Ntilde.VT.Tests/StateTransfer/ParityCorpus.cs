using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// The byte streams the parity test replays. Two sources, for two different kinds of confidence:
/// recorded fixtures are what real programs actually emit, and synthetic generators reach the
/// corners no recording happens to contain (a CSI long enough to truncate, a kitty payload
/// chunked with m=1, a mode nothing in the fixtures enables).
/// </summary>
internal static class ParityCorpus
{
    /// <summary>
    /// Fewest recorded streams <see cref="Recorded"/> will accept before declaring the fixture
    /// wiring broken. A floor, not an equality, so adding a <c>.rec</c> fixture does not break the
    /// guard - it only ever has to notice fixtures going *missing*.
    /// </summary>
    /// <remarks>
    /// The guard exists because the silent version of this failure is the worst thing the
    /// instrument could do. If the linked <c>Content</c> item in Ntilde.VT.Tests.csproj stops
    /// resolving - the fixture folder moves inside Ntilde.App.Tests, the <c>Link</c> metadata
    /// changes, a case-sensitive CI runner disagrees about "Fixtures/Replay" - the corpus would
    /// quietly shrink to its synthetic half and the parity suite would still report success, over
    /// a corpus it never loaded. Absent fixtures must be an error, not a smaller corpus.
    /// </remarks>
    public const int MinimumRecordedStreams = 16;

    public static IEnumerable<(string Name, byte[] Bytes)> All()
    {
        foreach ((string name, byte[] bytes) in Synthetic())
        {
            yield return (name, bytes);
        }

        foreach ((string name, byte[] bytes) in Recorded())
        {
            yield return (name, bytes);
        }
    }

    /// <summary>
    /// Data payloads of the linked replay fixtures, concatenated per file.
    /// </summary>
    /// <remarks>
    /// Through <c>ReplayReader.RunAsync</c> rather than by parsing the NDJSON here, because that
    /// is the code that knows a v2 file leads with a header line and a v1 file does not - and the
    /// fixture set contains both (<c>hello_world.rec</c> has a header; <c>vttest_cursor.rec</c>
    /// starts straight in on events). Resize, marker, input and snapshot events are ignored: the
    /// corpus is a byte stream, and the harness drives resizes itself at offsets it chooses.
    /// </remarks>
    public static IEnumerable<(string Name, byte[] Bytes)> Recorded()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Replay");
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException(
                $"Replay fixture directory '{dir}' does not exist, so the parity corpus would be " +
                $"its synthetic half only. Expected at least {MinimumRecordedStreams} '*.rec' " +
                "fixtures, linked into the output by the Content item in Ntilde.VT.Tests.csproj " +
                "from tests/Ntilde.App.Tests/Fixtures/Replay. Fix the link rather than lowering " +
                "the floor: a parity run over a corpus that silently failed to load reports " +
                "success while testing almost nothing.");
        }

        int yielded = 0;
        foreach (string path in Directory.GetFiles(dir, "*.rec").OrderBy(p => p, StringComparer.Ordinal))
        {
            var bytes = new List<byte>();
            var reader = new Ntilde.Replay.ReplayReader(path);

            reader.RunAsync(
                onDataCallback: data =>
                {
                    bytes.AddRange(data);
                    return Task.CompletedTask;
                },
                realtime: false).GetAwaiter().GetResult();

            if (bytes.Count > 0)
            {
                yielded++;
                yield return (Path.GetFileNameWithoutExtension(path), bytes.ToArray());
            }
        }

        if (yielded < MinimumRecordedStreams)
        {
            throw new InvalidOperationException(
                $"Replay fixture directory '{dir}' yielded {yielded} non-empty stream(s), fewer " +
                $"than the expected minimum of {MinimumRecordedStreams}. Either fixtures went " +
                "missing from tests/Ntilde.App.Tests/Fixtures/Replay, the Content link in " +
                "Ntilde.VT.Tests.csproj stopped resolving, or a fixture stopped parsing. Fix the " +
                "cause rather than lowering the floor: a parity run over a corpus that silently " +
                "failed to load reports success while testing almost nothing.");
        }
    }

    public static IEnumerable<(string Name, byte[] Bytes)> Synthetic()
    {
        yield return ("csi-basic", Utf8(
            "\u001b[2J\u001b[H" +
            "\u001b[1;1Hrow one\u001b[2;1Hrow two\u001b[3;5Hindented\r\n" +
            "\u001b[10G\u001b[K\u001b[4A\u001b[2B\u001b[3C\u001b[1D" +
            "\u001b[2L\u001b[1M\u001b[3@\u001b[2P\u001b[5X"));

        yield return ("csi-long-params", Utf8(
            "before" + "\u001b[" + new string('1', 200) + ";2;3;4;5;6;7;8;9;10m" + "after"));

        yield return ("csi-truncating", Utf8(
            // Past MaxCsiParamChars (65536), so _csiTruncated latches - the surviving prefix can
            // no longer be classified, and both sides must agree about that.
            "start\u001b[" + new string('9', 70_000) + "m" + "end"));

        yield return ("osc-titles-cwd", Utf8(
            "\u001b]0;window and icon\u0007" +
            "\u001b]2;window only\u001b\\" +
            "\u001b]7;file:///tmp/somewhere\u0007" +
            "text"));

        yield return ("osc8-hyperlinks", Utf8(
            "\u001b]8;id=alpha;https://example.com/a\u0007first\u001b]8;;\u0007 plain " +
            "\u001b]8;;https://example.com/b\u0007anonymous\u001b]8;;\u0007 " +
            "\u001b]8;id=alpha;https://example.com/a\u0007rejoins\u001b]8;;\u0007"));

        yield return ("osc52-clipboard", Utf8(
            "\u001b]52;c;aGVsbG8gY2xpcGJvYXJk\u0007after" +
            "\u001b]52;c;?\u0007"));

        yield return ("dcs", Utf8(
            "\u001bP0;1|somedcsdata\u001b\\visible" +
            "\u001bPtmux;\u001b\u001b[31m\u001b\\more"));

        yield return ("apc-kitty-chunked", Utf8(
            "\u001b_Ga=T,f=24,s=2,v=2,m=1;AAAAAAAA\u001b\\" +
            "\u001b_Gm=1;BBBBBBBB\u001b\\" +
            "\u001b_Gm=0;CCCCCCCC\u001b\\" +
            "after image"));

        yield return ("alt-screen", Utf8(
            "main content here\r\n" +
            "\u001b[?1049h" + "\u001b[2J\u001b[Halt content\r\nsecond alt row" +
            "\u001b[?1049l" + "back on main\r\n" +
            "\u001b[?1049h" + "and in again"));

        // The `?1049h` stream above can never reach EnterAltScreen(clearAlt: false), because 1049
        // always clears on entry. `?47h` is the only spelling that does not, and retained alt
        // content is exactly what went wrong in the snapshot's alt-screen shape handling - so the
        // non-clearing path gets a stream of its own. `?1047h` (clears, but does not save the
        // cursor) sits in between and is here for the same reason.
        yield return ("alt-screen-no-clear", Utf8(
            "main row one\r\nmain row two\r\n" +
            "\u001b[?1047h" + "\u001b[1;1Halt content written under 1047" +
            "\u001b[?1047l" + "\u001b[3;1Hmain again" +
            // Re-entry WITHOUT clearing: the 1047 row above must still be on the alt screen.
            "\u001b[?47h" + "\u001b[2;1Halt content written under 47" +
            "\u001b[?47l" + "\u001b[4;1Hmain third row" +
            // And once more, so the stream ends on an alt screen holding two retained rows.
            "\u001b[?47h" + "\u001b[5;1Hre-entered, prior alt rows retained"));

        yield return ("scroll-regions", Utf8(
            "\u001b[2J\u001b[H" +
            "\u001b[5;15r" + "\u001b[6;1Hinside region\r\n" +
            FillerRows(12) +
            "\u001b[r" + "\u001b[1;1Hfull screen again"));

        yield return ("tab-stops", Utf8(
            "\u001b[3g" +
            "\u001b[1;5H\u001bH" +
            "\u001b[1;13H\u001bH" +
            "\u001b[1;1Ha\tb\tc\r\n" +
            "\u001b[1;13H\u001b[g" +
            "\u001b[2;1Hx\ty\tz"));

        yield return ("decsc-decrc", Utf8(
            "\u001b[8;20H\u001b[31;1m\u001b7" +
            "\u001b[1;1H\u001b[0mreset here" +
            "\u001b8restored"));

        yield return ("charsets", Utf8(
            "\u001b(0" + "qwertyuiop" + "\u001b(B" + "qwertyuiop" +
            "\u001b)0" + "\u000e" + "asdfgh" + "\u000f" + "asdfgh"));

        yield return ("sgr-256-and-rgb", Utf8(
            "\u001b[38;5;196mindexed fg\u001b[48;5;21m indexed bg" +
            "\u001b[38;2;12;34;56mrgb fg\u001b[48;2;200;100;50m rgb bg" +
            "\u001b[1;3;4;5;7;9mall attrs\u001b[0m plain"));

        yield return ("wide-and-graphemes", Utf8(
            "你好世界 ascii テスト\r\n" +
            // Emoji, a regional-indicator flag pair, and a ZWJ family cluster.
            "\U0001F600\U0001F1EC\U0001F1E7\U0001F468‍\U0001F469‍\U0001F467\r\n" +
            "éà combining\r\n" +
            // A wide char landing exactly on the last column, which is the wrap corner.
            "\u001b[1;79H你好"));

        yield return ("kitty-keyboard", Utf8(
            "\u001b[>1u" + "\u001b[>1u" + "\u001b[=1;2u" + "\u001b[<1u" + "\u001b[?u" +
            "\u001b[?1049h" + "\u001b[>1u" + "\u001b[?1049l"));

        yield return ("synchronized-output", Utf8(
            "\u001b[?2026h" + "batched text one\r\nbatched text two" + "\u001b[?2026l" +
            "\u001b[?2026h" + "left open"));

        yield return ("mode-2048-inband-resize", Utf8(
            "\u001b[?2048h" + "client wants in-band reports" + "\u001b[?2048l" +
            "\u001b[?2048h"));

        yield return ("mixed-modes", Utf8(
            "\u001b[?1h\u001b[?7l\u001b[?6h\u001b[?25l\u001b[?2004h\u001b[?1000h\u001b[?1006h" +
            "\u001b[4h\u001b[20h" +
            "text under all of those"));
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    /// <paramref name="count"/> newline-terminated rows, enough output for a scroll region to
    /// scroll several times over. The content only has to be distinguishable row to row.
    /// </summary>
    private static string FillerRows(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append("filler row ").Append(i).Append("\r\n");
        }

        return sb.ToString();
    }
}
