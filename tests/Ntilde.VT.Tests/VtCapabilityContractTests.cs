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
    private static void AssertOscCellSize(string request, string expected)
    {
        var parser = new AnsiParser(new TerminalBuffer(80, 24)) { CellSizeReports = new(8, 18, 2, 13) };
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        parser.Process(request);
        Assert.Equal(expected, Assert.Single(responses));
    }

    private static readonly Dictionary<string, Action> ContractCases =
        new(StringComparer.Ordinal)
        {
            ["decrqss-top-bottom-margins"] = AssertDecrqssMargins,
            ["decrqss-left-right-margins"] = AssertDecrqssLeftRightMargins,
            ["decrqss-sgr"] = AssertDecrqssSgr,
            ["decrqss-cursor-style"] = AssertDecrqssCursorStyle,
            ["xtgettcap"] = AssertXtgettcap,
            ["xtsmgraphics"] = AssertXtsmgraphics,
            ["tertiary-device-attributes"] = AssertTertiaryDeviceAttributes,
            ["text-area-size-query"] = AssertTextAreaSizeQuery,
            ["title-stack-push"] = AssertTitleStack,
            ["title-stack-pop"] = AssertTitleStack,
            ["cursor-next-line"] = AssertCursorNextLine,
            ["cursor-previous-line"] = AssertCursorPreviousLine,
            ["cursor-horizontal-absolute"] = AssertCursorHorizontalAbsolute,
            ["erase-rectangular-area"] = AssertRectangularTextErase,
            ["fill-rectangular-area"] = AssertRectangularTextFill,
            ["copy-rectangular-area"] = AssertRectangularTextCopy,
            ["change-attributes-rectangular-area"] = AssertRectangularAttributeChange,
            ["reverse-attributes-rectangular-area"] = AssertRectangularAttributeReverse,
            ["select-attribute-change-extent"] = AssertAttributeChangeExtent,
            ["insert-column"] = () => AssertColumnEdit("\u001b[2'}", "AB  CD", "GH  IJ"),
            ["delete-column"] = () => AssertColumnEdit("\u001b[2'~", "ABEF  ", "GHKL  "),
            ["scroll-left"] = () => AssertColumnEdit("\u001b[2 @", "CDEF  ", "IJKL  "),
            ["scroll-right"] = () => AssertColumnEdit("\u001b[2 A", "  ABCD", "  GHIJ"),
            ["rectangle-checksum"] = AssertRectangleChecksum,
            ["icon-label-report"] = AssertIconLabelReport,
            ["window-title-report"] = AssertWindowTitleReport,
            ["xtgetxres"] = AssertXtgetxres,
            ["xterm-color-stack-push"] = AssertXtermColorStack,
            ["xterm-color-stack-pop"] = AssertXtermColorStack,
            ["xterm-color-stack-report"] = AssertXtermColorStack,
            ["xterm-sgr-stack-push"] = AssertXtermSgrStack,
            ["xterm-sgr-stack-pop"] = AssertXtermSgrStack,
            ["xterm-mode-save"] = AssertXtermModeSave,
            ["xterm-mode-restore"] = AssertXtermModeSave,
            ["left-right-margin-mode"] = AssertLeftRightMarginMode,
            ["set-left-right-margins"] = AssertSetLeftRightMargins,
            ["color-stack-push"] = AssertRuntimeColorStack,
            ["color-stack-pop"] = AssertRuntimeColorStack,
            ["kitty-color-control"] = AssertKittyColorControl,
            ["osc5-special-color"] = AssertOsc5SpecialColor,
            ["osc3008-context"] = AssertOsc3008Context,
            ["osc22-pointer"] = AssertOsc22Pointer,
            ["color-scheme-query"] = AssertColorSchemeQuery,
            ["color-scheme-notifications"] = AssertColorSchemeNotifications,
            ["alternate-scroll"] = AssertAlternateScroll,
            ["utf8-mouse-coordinates"] = () => AssertMouseEncodingSelection(1005),
            ["urxvt-mouse-coordinates"] = () => AssertMouseEncodingSelection(1015),
            ["pixel-mouse-coordinates"] = () => AssertMouseEncodingSelection(1016),
            ["mime-clipboard-paste"] = AssertMimeClipboardPaste,
            ["sgr-overline-on"] = AssertOverline,
            ["sgr-overline-off"] = AssertOverline,
            ["modify-other-keys-query"] = AssertModifyOtherKeysQuery,
            ["iterm2-cell-size"] = () => AssertOscCellSize("\x1b]1337;ReportCellSize\a", "\x1b]1337;ReportCellSize=18;8;2\x1b\\"),
            ["urxvt-cell-size"] = () => AssertOscCellSize("\x1b]776;?\a", "\x1b]776;16;36;26\a"),
        };

    [Fact]
    public void Partial_osc5_special_color_catalogue_entry_has_an_executable_contract()
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == "OSC:5");
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal("osc5-special-color", capability.ContractCase);
        ContractCases["osc5-special-color"]();
    }

    [Fact]
    public void Partial_osc3008_context_catalogue_entry_has_an_executable_contract()
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == "OSC:3008");
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal("osc3008-context", capability.ContractCase);
        ContractCases["osc3008-context"]();
    }

    private static readonly Dictionary<string, string> ContractSequences =
        new(StringComparer.Ordinal)
        {
            ["decrqss-top-bottom-margins"] = "\x1bP$qr\x1b\\",
            ["decrqss-left-right-margins"] = "\x1bP$qs\x1b\\",
            ["decrqss-sgr"] = "\x1bP$qm\x1b\\",
            ["decrqss-cursor-style"] = "\x1bP$q q\x1b\\",
            ["xtgettcap"] = "\x1bP+q436f;544e\x1b\\",
            ["xtsmgraphics"] = "\x1b[?1;1;0S\x1b[?2;1;0S",
            ["tertiary-device-attributes"] = "\x1b[=0c",
            ["text-area-size-query"] = "\x1b[18t",
            ["title-stack-push"] = "\x1b]2;saved\a\x1b[22;0t",
            ["title-stack-pop"] = "\x1b]2;saved\a\x1b[22;0t\x1b]2;vim\a\x1b[23;0t",
            ["cursor-next-line"] = "\x1b[2E",
            ["cursor-previous-line"] = "\x1b[2F",
            ["cursor-horizontal-absolute"] = "\x1b[4G",
            ["erase-rectangular-area"] = "\u001b[2;2;3;3$z",
            ["fill-rectangular-area"] = "\u001b[88;2;2;3;3$x",
            ["copy-rectangular-area"] = "\u001b[1;1;2;2;1;2;2$v",
            ["change-attributes-rectangular-area"] = "\u001b[2*x\u001b[2;2;3;3;7$r",
            ["reverse-attributes-rectangular-area"] = "\u001b[2*x\u001b[2;2;3;3;7$t",
            ["select-attribute-change-extent"] = "\u001b[2*x",
            ["insert-column"] = "ABCDEF\r\nGHIJKL\u001b[1;3H\u001b[2'}",
            ["delete-column"] = "ABCDEF\r\nGHIJKL\u001b[1;3H\u001b[2'~",
            ["scroll-left"] = "ABCDEF\r\nGHIJKL\u001b[1;3H\u001b[2 @",
            ["scroll-right"] = "ABCDEF\r\nGHIJKL\u001b[1;3H\u001b[2 A",
            ["rectangle-checksum"] = "ABCDE\u001b[1;1H\u001b[1;1;1;1;1;5*y",
            ["icon-label-report"] = "\u001b]1;test-icon\a\u001b[20t",
            ["window-title-report"] = "\u001b]2;test-title\a\u001b[21t",
            ["xtgetxres"] = "\u001bP+Q7465726d4e616d65\u001b\\",
            ["xterm-color-stack-push"] = "\u001b]10;#123456\a\u001b[#P",
            ["xterm-color-stack-pop"] = "\u001b[#P\u001b]10;#abcdef\a\u001b[#Q",
            ["xterm-color-stack-report"] = "\u001b[#P\u001b[#R",
            ["xterm-sgr-stack-push"] = "\u001b[1m\u001b[#{",
            ["xterm-sgr-stack-pop"] = "\u001b[#{\u001b[1m\u001b[#}",
            ["xterm-mode-save"] = "\u001b[?7s\u001b[?7l",
            ["xterm-mode-restore"] = "\u001b[?7s\u001b[?7l\u001b[?7r",
            ["left-right-margin-mode"] = "\u001b[?69h\u001b[?69$p",
            ["set-left-right-margins"] = "\u001b[?69h\u001b[3;5s\u001b[1;4H12345",
            ["color-stack-push"] = "\u001b]10;#123456\a\u001b]30001\a",
            ["color-stack-pop"] = "\u001b]30001\a\u001b]10;#abcdef\a\u001b]30101\a",
            ["kitty-color-control"] = "\u001b]21;foreground=#123456;foreground=?\u001b\\",
            ["color-scheme-query"] = "\x1b[?996n",
            ["color-scheme-notifications"] = "\x1b[?2031h\x1b[?2031$p",
            ["alternate-scroll"] = "\x1b[?1007l\x1b[?1007$p",
            ["utf8-mouse-coordinates"] = "\x1b[?1005h\x1b[?1005$p",
            ["urxvt-mouse-coordinates"] = "\x1b[?1015h\x1b[?1015$p",
            ["pixel-mouse-coordinates"] = "\x1b[?1016h\x1b[?1016$p",
            ["mime-clipboard-paste"] = "\x1b[?5522h\x1b[?5522$p",
            ["sgr-overline-on"] = "\x1b[53mA",
            ["sgr-overline-off"] = "\x1b[53mA\x1b[55mB",
            ["modify-other-keys-query"] = "\x1b[>4;2m\x1b[?4mA",
            ["iterm2-cell-size"] = "\x1b]1337;ReportCellSize\aA",
            ["urxvt-cell-size"] = "\x1b]776;?\x1b\\A",
        };

    /// <summary>Pins the advertised partial text contract without claiming image-plane erasure or protected-cell support.</summary>
    [Fact]
    public void Partial_DECERA_catalogue_has_an_executable_text_contract()
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == "CSI:$z");
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal("erase-rectangular-area", capability.ContractCase);
        ContractCases["erase-rectangular-area"]();
    }

    /// <summary>Pins the advertised partial column contracts (DECIC, DECDC, SL, SR) without claiming left/right margins.</summary>
    [Theory]
    [InlineData("CSI:'}", "DECIC", "insert-column")]
    [InlineData("CSI:'~", "DECDC", "delete-column")]
    [InlineData("CSI:SP@", "SL", "scroll-left")]
    [InlineData("CSI:SPA", "SR", "scroll-right")]
    public void Partial_column_catalogue_entries_have_executable_text_contracts(string key, string mnemonic, string contractCase)
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == key);
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal(mnemonic, capability.Mnemonic);
        Assert.Equal(contractCase, capability.ContractCase);
        ContractCases[contractCase]();
    }

    /// <summary>Pins the advertised partial DECLRMM and DECSLRM contracts.</summary>
    [Theory]
    [InlineData("DEC:69", "DECLRMM", "left-right-margin-mode")]
    [InlineData("CSI:s", "DECSLRM", "set-left-right-margins")]
    public void Partial_margin_catalogue_entries_have_executable_contracts(string key, string mnemonic, string contractCase)
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == key);
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal(mnemonic, capability.Mnemonic);
        Assert.Equal(contractCase, capability.ContractCase);
        ContractCases[contractCase]();
    }

    /// <summary>DECRQM reports DECLRMM; without it CSI s saves the cursor, with it CSI s sets margins.</summary>
    private static void AssertLeftRightMarginMode()
    {
        var buffer = new TerminalBuffer(10, 3);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\u001b[?69$p\u001b[?69h\u001b[?69$p\u001b[3;5s");
        Assert.Equal(new[] { "\u001b[?69;2$y", "\u001b[?69;1$y" }, responses);
        Assert.Equal((2, 4), (buffer.LeftMargin, buffer.RightMargin));
        parser.Process("\u001b[?69l");
        Assert.Equal((0, 9), (buffer.LeftMargin, buffer.RightMargin));
    }

    /// <summary>Margins 3-5: text printed from column 4 wraps at the right margin back to the left margin.</summary>
    private static void AssertSetLeftRightMargins()
    {
        var buffer = new TerminalBuffer(10, 3);
        new AnsiParser(buffer).Process("\u001b[?69h\u001b[3;5s\u001b[1;4H12345");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("   12     ", new string(Enumerable.Range(0, 10).Select(col => buffer.GetCell(col, 0).Character is '\0' ? ' ' : buffer.GetCell(col, 0).Character).ToArray()));
            Assert.Equal("  345     ", new string(Enumerable.Range(0, 10).Select(col => buffer.GetCell(col, 1).Character is '\0' ? ' ' : buffer.GetCell(col, 1).Character).ToArray()));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>Pins the advertised partial DECRQCRA contract: silent by default, the checksum reply once the host gate is on.</summary>
    [Fact]
    public void Partial_DECRQCRA_catalogue_entry_is_gated_and_has_an_executable_contract()
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == "CSI:*y");
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal("DECRQCRA", capability.Mnemonic);
        Assert.Equal("rectangle-checksum", capability.ContractCase);
        ContractCases["rectangle-checksum"]();
    }

    /// <summary>The pinned terminfo.dev seed: A-E sum to 0x14F, so the negated 16-bit checksum is FEB1; the default gate stays silent.</summary>
    private static void AssertRectangleChecksum()
    {
        var silent = new List<string>();
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = silent.Add }.Process("ABCDE\u001b[1;1H\u001b[1;1;1;1;1;5*y");
        Assert.Empty(silent);
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = responses.Add, AllowRectangleChecksumReport = true }
            .Process("ABCDE\u001b[1;1H\u001b[1;1;1;1;1;5*y");
        Assert.Equal("\u001bP1!~FEB1\u001b\\", Assert.Single(responses));
    }

    /// <summary>CSI 20 t: silent by default; with the gate on, the icon name OSC 1 set in xterm's OSC L form.</summary>
    private static void AssertIconLabelReport()
    {
        var silent = new List<string>();
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = silent.Add }.Process("\u001b]1;test-icon\a\u001b[20t");
        Assert.Empty(silent);
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = responses.Add, AllowTitleReport = true }.Process("\u001b]1;test-icon\a\u001b[20t");
        Assert.Equal("\u001b]Ltest-icon\u001b\\", Assert.Single(responses));
    }

    /// <summary>CSI 21 t: silent by default; with the gate on, the window title OSC 2 set in xterm's OSC l form.</summary>
    private static void AssertWindowTitleReport()
    {
        var silent = new List<string>();
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = silent.Add }.Process("\u001b]2;test-title\a\u001b[21t");
        Assert.Empty(silent);
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = responses.Add, AllowTitleReport = true }.Process("\u001b]2;test-title\a\u001b[21t");
        Assert.Equal("\u001b]ltest-title\u001b\\", Assert.Single(responses));
    }

    /// <summary>termName answers with the host-supplied TERM; without one it is DCS 0 + R.</summary>
    private static void AssertXtgetxres()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = responses.Add, TerminalTypeName = "xterm-256color" }
            .Process("\u001bP+Q7465726d4e616d65\u001b\\");
        new AnsiParser(new TerminalBuffer(8, 2)) { OnResponse = responses.Add }.Process("\u001bP+Q7465726d4e616d65\u001b\\");
        Assert.Equal(new[] { "\u001bP1+R7465726d4e616d65=787465726D2D323536636F6C6F72\u001b\\", "\u001bP0+R7465726d4e616d65\u001b\\" }, responses);
    }

    /// <summary>Push saves the pen's attributes silently; pop restores them.</summary>
    private static void AssertXtermSgrStack()
    {
        var buffer = new TerminalBuffer(8, 2);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\u001b[1;31m\u001b[#{\u001b[0m");
        Assert.False(buffer.IsBold);
        parser.Process("\u001b[#}");
        Assert.True(buffer.IsBold);
        Assert.Equal(1, buffer.CurrentFgIndex);
        Assert.Empty(responses);
    }

    /// <summary>Save keeps the mode's value silently; restore applies it again.</summary>
    private static void AssertXtermModeSave()
    {
        var buffer = new TerminalBuffer(8, 2);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\u001b[?7s\u001b[?7l");
        Assert.False(buffer.Modes.IsAutoWrapMode);
        parser.Process("\u001b[?7r");
        Assert.True(buffer.Modes.IsAutoWrapMode);
        Assert.Empty(responses);
    }

    /// <summary>Push, change, pop restores; the report is xterm's CSI ? depth ; deepest # Q.</summary>
    private static void AssertXtermColorStack()
    {
        var buffer = new TerminalBuffer(8, 2);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\u001b]10;#123456\a\u001b[#P\u001b[#R\u001b]10;#abcdef\a\u001b[#Q\u001b[#R");
        Assert.Equal(new[] { "\u001b[?1;1#Q", "\u001b[?0;1#Q" }, responses);
        Assert.Equal(0xff123456u, buffer.ExportState(0).Colors!.Current.Foreground);
    }

    /// <summary>Two seeded rows, the cursor at row 1 column 3: both rows of the default region move and the cursor stays put.</summary>
    private static void AssertColumnEdit(string control, string first, string second)
    {
        var buffer = new TerminalBuffer(6, 2);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("ABCDEF\r\nGHIJKL\u001b[1;3H" + control);
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(first, new string(Enumerable.Range(0, 6).Select(col => buffer.GetCell(col, 0).Character).ToArray()));
            Assert.Equal(second, new string(Enumerable.Range(0, 6).Select(col => buffer.GetCell(col, 1).Character).ToArray()));
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal((0, 2, false), (buffer.CursorRow, buffer.CursorCol, buffer.IsPendingWrap));
    }

    /// <summary>Pins the advertised partial fill contract without claiming image-plane, protected-cell or non-Latin-1 fill.</summary>
    [Fact]
    public void Partial_DECFRA_catalogue_has_an_executable_text_contract()
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == "CSI:$x");
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal("fill-rectangular-area", capability.ContractCase);
        ContractCases["fill-rectangular-area"]();
    }

    /// <summary>Pins the advertised partial copy contract: page 1 only, text plane only.</summary>
    [Fact]
    public void Partial_DECCRA_catalogue_has_an_executable_text_contract()
    {
        var capability = Assert.Single(VtCapabilityCatalog.All, item => item.Key == "CSI:$v");
        Assert.Equal(VtSupport.Partial, capability.Support);
        Assert.Equal("copy-rectangular-area", capability.ContractCase);
        ContractCases["copy-rectangular-area"]();
    }

    private static void AssertRectangularTextFill()
    {
        var buffer = new TerminalBuffer(4, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("AAAA\r\nBBBB\r\nCCCC\u001b[88;2;2;3;3$x");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal('A', buffer.GetCell(1, 0).Character);
            Assert.Equal('B', buffer.GetCell(0, 1).Character);
            Assert.Equal('X', buffer.GetCell(1, 1).Character);
            Assert.Equal('X', buffer.GetCell(2, 2).Character);
            Assert.Equal('C', buffer.GetCell(3, 2).Character);
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal((2, 3, true), (buffer.CursorRow, buffer.CursorCol, buffer.IsPendingWrap));
    }

    private static void AssertRectangularTextCopy()
    {
        var buffer = new TerminalBuffer(4, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("AAAA\r\nBBBB\r\nCCCC\u001b[1;1;2;2;1;2;2$v");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal('A', buffer.GetCell(0, 0).Character);
            Assert.Equal('B', buffer.GetCell(0, 1).Character);
            Assert.Equal('A', buffer.GetCell(1, 1).Character);
            Assert.Equal('A', buffer.GetCell(2, 1).Character);
            Assert.Equal('B', buffer.GetCell(1, 2).Character);
            Assert.Equal('B', buffer.GetCell(2, 2).Character);
            Assert.Equal('C', buffer.GetCell(0, 2).Character);
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal((2, 3, true), (buffer.CursorRow, buffer.CursorCol, buffer.IsPendingWrap));
    }

    /// <summary>Checks a real rectangle in authored rows, including preservation of pending wrap at the last column.</summary>
    private static void AssertRectangularTextErase()
    {
        var buffer = new TerminalBuffer(4, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("AAAA\r\nBBBB\r\nCCCC\u001b[2;2;3;3$z");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal('A', buffer.GetCell(1, 0).Character);
            Assert.Equal('B', buffer.GetCell(0, 1).Character);
            Assert.Equal(' ', buffer.GetCell(1, 1).Character);
            Assert.Equal(' ', buffer.GetCell(2, 2).Character);
            Assert.Equal('C', buffer.GetCell(3, 2).Character);
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal((2, 3, true), (buffer.CursorRow, buffer.CursorCol, buffer.IsPendingWrap));
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser) AttributeFixture()
    {
        var buffer = new TerminalBuffer(4, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("AAAA\r\nBBBB\r\nCCCC");
        return (buffer, parser);
    }

    private static string InverseMap(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return string.Join("|", Enumerable.Range(0, buffer.Rows).Select(row => new string(Enumerable.Range(0, buffer.Cols)
                .Select(col => buffer.GetCell(col, row).IsInverse ? 'I' : '.').ToArray())));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static void AssertRectangularAttributeChange()
    {
        var (buffer, parser) = AttributeFixture();
        parser.Process("\u001b[2*x\u001b[2;2;3;3;7$r");
        Assert.Equal("....|.II.|.II.", InverseMap(buffer));
        parser.Process("\u001b[2;2;3;3;27$r");
        Assert.Equal("....|....|....", InverseMap(buffer));
    }

    private static void AssertRectangularAttributeReverse()
    {
        var (buffer, parser) = AttributeFixture();
        parser.Process("\u001b[2*x\u001b[2;2;3;3;7$t");
        Assert.Equal("....|.II.|.II.", InverseMap(buffer));
        parser.Process("\u001b[2;2;3;3;7$t");
        Assert.Equal("....|....|....", InverseMap(buffer));
    }

    private static void AssertAttributeChangeExtent()
    {
        var (buffer, parser) = AttributeFixture();
        parser.Process("\u001b[1;2;3;3;7$r");
        Assert.Equal(".III|IIII|III.", InverseMap(buffer));
        var (rectangle, rectangleParser) = AttributeFixture();
        rectangleParser.Process("\u001b[2*x\u001b[1;2;3;3;7$r");
        Assert.Equal(".II.|.II.|.II.", InverseMap(rectangle));
    }

    private static void AssertDecrqssMargins()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }
            .Process("\x1b[2;5r\x1bP$qr\x1b\\");
        Assert.Equal("\x1bP1$r2;5r\x1b\\", Assert.Single(responses));
    }

    private static void AssertDecrqssLeftRightMargins()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }
            .Process("\x1bP$qs\x1b\\\x1b[?69h\x1b[5;14s\x1bP$qs\x1b\\");
        Assert.Equal(new[] { "\x1bP1$r1;40s\x1b\\", "\x1bP1$r5;14s\x1b\\" }, responses);
    }

    private static void AssertDecrqssSgr()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }
            .Process("\x1b[1;4:3;31;48;5;200m\x1bP$qm\x1b\\");
        Assert.Equal("\x1bP1$r0;1;4:3;31;48;5;200m\x1b\\", Assert.Single(responses));
    }

    private static void AssertXtgettcap()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }
            .Process("\x1bP+q436f;544e;5463\x1b\\\x1bP+q436f;zz\x1b\\");
        Assert.Equal(new[]
        {
            "\x1bP1+r436f=323536\x1b\\", "\x1bP0+r\x1b\\", "\x1bP1+r5463\x1b\\", "\x1bP0+r\x1b\\",
        }, responses);
    }

    private static void AssertDecrqssCursorStyle()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }
            .Process("\x1bP$q q\x1b\\\x1b[6 q\x1bP$q q\x1b\\");
        Assert.Equal(new[] { "\x1bP1$r0 q\x1b\\", "\x1bP1$r6 q\x1b\\" }, responses);
    }

    private static void AssertTertiaryDeviceAttributes()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }.Process("\x1b[=0c");
        Assert.Equal("\x1bP!|00000000\x1b\\", Assert.Single(responses));
    }

    private static void AssertXtsmgraphics()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(80, 24)) { CellWidth = 9, CellHeight = 18, OnResponse = responses.Add }
            .Process("\x1b[?1;1;0S\x1b[?2;1;0S\x1b[?2;3;640S");
        Assert.Equal(new[] { "\x1b[?1;0;4096S", "\x1b[?2;0;720;432S", "\x1b[?2;3S" }, responses);
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

    /// <summary>OSC 21 sets and queries the foreground, answering with one OSC 21 and a standard terminator.</summary>
    private static void AssertKittyColorControl()
    {
        var parser = new AnsiParser(new TerminalBuffer(12, 8));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b]21;foreground=#123456;foreground=?\u001b\\");
        Assert.Equal(new[] { "\u001b]21;foreground=rgb:12/34/56\u001b\\" }, replies);
    }

    /// <summary>rxvt OSC 5 sets and queries the bold special color in its own resource set, one standard frame per query.</summary>
    private static void AssertOsc5SpecialColor()
    {
        var parser = new AnsiParser(new TerminalBuffer(12, 8));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b]5;0;#123456;0;?\u001b\\");
        Assert.Equal(new[] { "\u001b]5;0;rgb:1212/3434/5656\u001b\\" }, replies);
    }

    /// <summary>OSC 3008 opens a context with its metadata and the matching end closes it; nothing is ever sent back.</summary>
    private static void AssertOsc3008Context()
    {
        var parser = new AnsiParser(new TerminalBuffer(12, 8));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b]3008;start=ctx;type=shell;user=u\u001b\\");
        var context = Assert.Single(parser.ContextStack);
        Assert.Equal(("ctx", "shell", "u"), (context.Id, context.Type, context.User));
        parser.Process("\u001b]3008;end=ctx\u001b\\");
        Assert.Empty(parser.ContextStack);
        Assert.Empty(replies);
    }

    private static void AssertOsc22Pointer()
    {
        var parser = new AnsiParser(new TerminalBuffer(12, 8));
        var requests = new List<(string?, bool)>();
        parser.OnPointerShapeRequested = (name, alternate) => requests.Add((name, alternate));
        parser.Process("\x1b]22;pointer\x07\x1b]22;text\x1b\\\x1b]22;\x07");
        Assert.Equal(new (string?, bool)[] { ("pointer", false), ("text", false), (null, false) }, requests);
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

    private static void AssertMouseEncodingSelection(int mode)
    {
        var buffer = new TerminalBuffer(12, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        int other = mode == 1005 ? 1015 : 1005;
        // Reset, set, replaced by ?1006 (single selection), set again, then a reset of a different encoding changes nothing.
        parser.Process($"\x1b[?{mode}$p\x1b[?{mode}h\x1b[?{mode}$p\x1b[?1006h\x1b[?{mode}$p\x1b[?{mode}h\x1b[?{other}l\x1b[?{mode}$p");
        Assert.Equal(new[]
        {
            $"\x1b[?{mode};2$y", $"\x1b[?{mode};1$y", $"\x1b[?{mode};2$y", $"\x1b[?{mode};1$y",
        }, replies);
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
