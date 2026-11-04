using Ntilde.VtContract;

namespace Ntilde.VT.Tests;

/// <summary>
/// One executable contract per capability the catalog advertises as
/// <see cref="VtSupport.Supported"/>: proof that something in the parser actually implements what
/// the catalog claims, rather than the claim standing alone.
/// </summary>
/// <remarks>
/// <para>
/// These assertions are <b>descriptive</b> - they record what the parser does today, not what a
/// specification says it ought to do. So when a change alters accepted behaviour on purpose,
/// updating the affected lines here is part of that change, not a warning sign. What must not
/// happen is the reverse: an assertion edited to make a build pass without the behaviour change
/// being intended and stated.
/// </para>
/// <para>
/// The distinction has bitten once, which is why it is written down. 816489b added
/// <c>AssertPosition("\x1b[?2G", ...)</c> and <c>AssertPosition("\x1b[2$G", ...)</c> by
/// transcribing what the parser happened to do at the time - CHA had no leader guard - while the
/// identical shapes for CNL and CPL three lines up had said <c>AssertIgnored</c> since b7a00dc.
/// The file contradicted itself for eight days. #433 changed the G lines to match, because a
/// leader or an intermediate makes a sequence a different sequence and neither
/// <c>CSI ? Ps G</c> nor <c>CSI Ps $ G</c> is defined. Behaviour here is also swept
/// systematically over every final byte in <c>AnsiParserHardeningTests</c>, so a contract line and
/// the sweep disagreeing is a real signal worth stopping for.
/// </para>
/// </remarks>
public sealed class VtCapabilityContractTests
{
    private static readonly Dictionary<string, Action> ContractCases =
        new(StringComparer.Ordinal)
        {
            ["decrqss-top-bottom-margins"] = AssertDecrqssMargins,
            ["tertiary-device-attributes"] = AssertTertiaryDeviceAttributes,
            ["text-area-size-query"] = AssertTextAreaSizeQuery,
            ["title-stack-push"] = AssertTitleStack,
            ["title-stack-pop"] = AssertTitleStack,
            ["cursor-next-line"] = AssertCursorNextLine,
            ["cursor-previous-line"] = AssertCursorPreviousLine,
            ["cursor-horizontal-absolute"] = AssertCursorHorizontalAbsolute,
            ["color-stack-push"] = AssertRuntimeColorStack,
            ["color-stack-pop"] = AssertRuntimeColorStack,
            ["color-scheme-query"] = AssertColorSchemeQuery,
            ["color-scheme-notifications"] = AssertColorSchemeNotifications,
            ["alternate-scroll"] = AssertAlternateScroll,
            ["mime-clipboard-paste"] = AssertMimeClipboardPaste,
            ["sgr-overline-on"] = AssertOverline,
            ["sgr-overline-off"] = AssertOverline,
            ["modify-other-keys-query"] = AssertModifyOtherKeysQuery,
        };

    private static readonly Dictionary<string, string> ContractSequences =
        new(StringComparer.Ordinal)
        {
            ["decrqss-top-bottom-margins"] = "\x1bP$qr\x1b\\",
            ["tertiary-device-attributes"] = "\x1b[=0c",
            ["text-area-size-query"] = "\x1b[18t",
            ["title-stack-push"] = "\x1b]2;saved\a\x1b[22;0t",
            ["title-stack-pop"] = "\x1b]2;saved\a\x1b[22;0t\x1b]2;vim\a\x1b[23;0t",
            ["cursor-next-line"] = "\x1b[2E",
            ["cursor-previous-line"] = "\x1b[2F",
            ["cursor-horizontal-absolute"] = "\x1b[4G",
            ["color-stack-push"] = "\u001b]10;#123456\a\u001b]30001\a",
            ["color-stack-pop"] = "\u001b]30001\a\u001b]10;#abcdef\a\u001b]30101\a",
            ["color-scheme-query"] = "\x1b[?996n",
            ["color-scheme-notifications"] = "\x1b[?2031h\x1b[?2031$p",
            ["alternate-scroll"] = "\x1b[?1007l\x1b[?1007$p",
            ["mime-clipboard-paste"] = "\x1b[?5522h\x1b[?5522$p",
            ["sgr-overline-on"] = "\x1b[53mA",
            ["sgr-overline-off"] = "\x1b[53mA\x1b[55mB",
            ["modify-other-keys-query"] = "\x1b[>4;2m\x1b[?4mA",
        };

    private static void AssertDecrqssMargins()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }
            .Process("\x1b[2;5r\x1bP$qr\x1b\\");
        Assert.Equal("\x1bP1$r2;5r\x1b\\", Assert.Single(responses));
    }

    private static void AssertTertiaryDeviceAttributes()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }.Process("\x1b[=0c");
        Assert.Equal("\x1bP!|00000000\x1b\\", Assert.Single(responses));
    }

    private static void AssertTextAreaSizeQuery()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(86, 30)) { OnResponse = responses.Add }.Process("\x1b[18t");
        Assert.Equal("\x1b[8;30;86t", Assert.Single(responses));
    }

    private static void AssertModifyOtherKeysQuery()
    {
        var responses = new List<string>();
        var buffer = new TerminalBuffer(40, 6);
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\x1b[?4m");
        parser.Process("\x1b[>4;2m\x1b[?4m");
        parser.Process("\x1b[?1049h\x1b[?4m\x1b[?1;2m");
        Assert.Equal(new[] { "\x1b[>4;0m", "\x1b[>4;2m", "\x1b[>4;0m" }, responses);
        Assert.False(buffer.IsUnderline);
    }

    private static void AssertTitleStack()
    {
        var titles = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnTitleChanged = titles.Add }
            .Process("\x1b]2;saved\a\x1b[22;0t\x1b]2;vim\a\x1b[23;0t\x1b[23;0t");
        Assert.Equal(new[] { "saved", "vim", "saved" }, titles);
    }

    private static void AssertOverline()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53mA\x1b[55mB\x1b[53mC\x1b[0mD");
        Assert.True(buffer.GetUnderlineAbsolute(0, 0)?.Overline);
        Assert.Null(buffer.GetUnderlineAbsolute(1, 0));
        Assert.True(buffer.GetUnderlineAbsolute(2, 0)?.Overline);
        Assert.Null(buffer.GetUnderlineAbsolute(3, 0));
    }

    public static IEnumerable<object[]> SupportedCapabilities()
        => VtCapabilityCatalog.All
            .Where(capability => capability.Support == VtSupport.Supported)
            .Select(capability => new object[]
            {
                capability.Key,
                capability.ContractCase!,
            });

    [Theory]
    [MemberData(nameof(SupportedCapabilities))]
    public void SupportedCapability_HasExecutableParserContract(string key, string contractCase)
    {
        bool registered = ContractCases.TryGetValue(contractCase, out Action? assertion);

        Assert.True(registered, $"Supported capability '{key}' has no parser contract registered as '{contractCase}'.");
        assertion!();
    }

    [Theory]
    [MemberData(nameof(SupportedCapabilities))]
    public void SupportedCapability_IsEquivalentAcrossEveryInputSplit(string key, string contractCase)
    {
        Assert.True(
            ContractSequences.TryGetValue(contractCase, out string? sequence),
            $"Supported capability '{key}' has no split-input sequence registered as '{contractCase}'.");

        (int expectedRow, int expectedCol) = ProcessAtEverySplit(sequence!, splitAt: null);
        for (int splitAt = 1; splitAt < sequence!.Length; splitAt++)
        {
            (int actualRow, int actualCol) = ProcessAtEverySplit(sequence, splitAt);
            Assert.Equal(expectedRow, actualRow);
            Assert.Equal(expectedCol, actualCol);
        }
    }

    private static void AssertMimeClipboardPaste()
    {
        var parser = new AnsiParser(new TerminalBuffer(12, 8)) { KittyClipboardEnabled = true };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\x1b[?5522$p\x1b[?5522h\x1b[?5522$p\x1b[?5522l\x1b[?5522$p");
        Assert.Equal(new[] { "\x1b[?5522;2$y", "\x1b[?5522;1$y", "\x1b[?5522;2$y" }, replies);
    }

    private static void AssertCursorNextLine()
    {
        AssertPosition("\x1b[E", expectedRow: 4, expectedCol: 0);
        AssertPosition("\x1b[0E", expectedRow: 4, expectedCol: 0);
        AssertPosition("\x1b[2E", expectedRow: 5, expectedCol: 0);
        AssertPosition("\x1b[2;3E", expectedRow: 5, expectedCol: 0);
        AssertPosition("\x1b[2:3E", expectedRow: 5, expectedCol: 0);
        AssertPosition("\x1b[999E", expectedRow: 7, expectedCol: 0);
        AssertIgnored("\x1b[?2E");
        AssertIgnored("\x1b[2$E");
    }

    private static void AssertCursorPreviousLine()
    {
        AssertPosition("\x1b[F", expectedRow: 2, expectedCol: 0);
        AssertPosition("\x1b[0F", expectedRow: 2, expectedCol: 0);
        AssertPosition("\x1b[2F", expectedRow: 1, expectedCol: 0);
        AssertPosition("\x1b[2;3F", expectedRow: 1, expectedCol: 0);
        AssertPosition("\x1b[2:3F", expectedRow: 1, expectedCol: 0);
        AssertPosition("\x1b[999F", expectedRow: 0, expectedCol: 0);
        AssertIgnored("\x1b[?2F");
        AssertIgnored("\x1b[2$F");
    }

    private static void AssertCursorHorizontalAbsolute()
    {
        AssertPosition("\x1b[G", expectedRow: 3, expectedCol: 0);
        AssertPosition("\x1b[0G", expectedRow: 3, expectedCol: 0);
        AssertPosition("\x1b[4G", expectedRow: 3, expectedCol: 3);
        AssertPosition("\x1b[2;3G", expectedRow: 3, expectedCol: 1);
        AssertPosition("\x1b[2:3G", expectedRow: 3, expectedCol: 1);
        AssertPosition("\x1b[999G", expectedRow: 3, expectedCol: 11);

        // #274: these two used to assert that CHA runs for the '?' leader and the '$'
        // intermediate. That was a transcription of what the parser happened to do - CHA had no
        // leader guard - rather than a decision, and it contradicted the identical shapes three
        // lines up in AssertCursorNextLine and AssertCursorPreviousLine, whose finals had already
        // been guarded by #264. A leader or an intermediate makes it a different sequence, so it
        // is ignored, exactly as CSI ? 2 E and CSI 2 $ F are.
        AssertIgnored("\x1b[?2G");
        AssertIgnored("\x1b[2$G");
    }

    private static void AssertRuntimeColorStack()
    {
        var buffer = new TerminalBuffer(12, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b]10;#123456\a\u001b]30001\a\u001b]10;#abcdef\a\u001b]30101\a");
        Assert.Equal(0xff123456u, buffer.ExportState(0).Colors!.Current.Foreground);
        Assert.Empty(buffer.ExportState(0).Colors!.Stack);
    }

    private static void AssertColorSchemeQuery()
    {
        var parser = new AnsiParser(new TerminalBuffer(12, 8)) { DefaultBackground = TermColor.White };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[?996n");
        Assert.Equal(new[] { "\u001b[?997;2n" }, replies);
    }

    private static void AssertColorSchemeNotifications()
    {
        var parser = new AnsiParser(new TerminalBuffer(12, 8));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[?2031h\u001b[?2031$p");
        parser.DefaultBackground = TermColor.White;
        Assert.Equal(new[] { "\u001b[?2031;1$y", "\u001b[?997;2n" }, replies);
        parser.Process("\u001b[?2031l");
        parser.DefaultBackground = TermColor.Black;
        Assert.Equal(2, replies.Count);
    }

    private static void AssertAlternateScroll()
    {
        var buffer = new TerminalBuffer(12, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        // Untouched, the mode reports the host default (on unless the host turns it off).
        parser.Process("\x1b[?1007$p\x1b[?1007l\x1b[?1007$p");
        buffer.Modes.AlternateScrollDefault = false;
        parser.Process("\x1b[?1007h\x1b[?1007$p\u001bc\x1b[?1007$p");
        Assert.Equal(new[] { "\x1b[?1007;1$y", "\x1b[?1007;2$y", "\x1b[?1007;1$y", "\x1b[?1007;2$y" }, replies);
    }

    private static void AssertPosition(string sequence, int expectedRow, int expectedCol)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        parser.Process(sequence);

        Assert.Equal(expectedRow, buffer.CursorRow);
        Assert.Equal(expectedCol, buffer.CursorCol);
    }

    private static void AssertIgnored(string sequence)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        parser.Process(sequence);

        Assert.Equal(3, buffer.CursorRow);
        Assert.Equal(5, buffer.CursorCol);
    }

    private static (int Row, int Col) ProcessAtEverySplit(string sequence, int? splitAt)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        if (splitAt is int index)
        {
            parser.Process(sequence[..index]);
            parser.Process(sequence[index..]);
        }
        else
        {
            parser.Process(sequence);
        }

        return (buffer.CursorRow, buffer.CursorCol);
    }
}
