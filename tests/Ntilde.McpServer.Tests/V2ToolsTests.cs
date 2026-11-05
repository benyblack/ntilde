using System.Text.RegularExpressions;
using Ntilde.McpServer.Tools;
using Ntilde.VtContract;

namespace Ntilde.McpServer.Tests;

public class ExplainEscapeSequenceTests
{
    [Theory]
    [InlineData("ESC[2J", "ED")]
    [InlineData("\\x1b[2J", "ED")]
    [InlineData("CSI 2 J", "ED")]
    [InlineData("CSI H", "CUP")]
    [InlineData("CSI ?25h", "DECSET")]      // final byte 'h'
    [InlineData("CSI m", "SGR")]
    [InlineData("OSC 7", "working directory")]
    [InlineData("OSC 8", "Hyperlink")]
    [InlineData("ESC c", "RIS")]
    [InlineData("ESC [ 2 J", "ED")]          // space form
    [InlineData("ESC ] 7", "working directory")]
    [InlineData("ESC P q", "DCS")]           // 7-bit DCS introducer
    [InlineData("ESCP", "DCS")]              // no-space DCS introducer
    [InlineData("ESC _ G", "APC")]           // 7-bit APC introducer
    [InlineData("ESC_Ga=T", "APC")]          // no-space APC (Kitty)
    public void RecognizesCommonSequences(string seq, string expectedSubstring)
    {
        Assert.Contains(expectedSubstring, VtTools.ExplainEscapeSequence(seq), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Osc52_IsMarkedUnsupported()
    {
        // Ntilde's AnsiParser does not handle OSC 52; the explainer must not imply it does.
        var result = VtTools.ExplainEscapeSequence("OSC 52");
        Assert.Contains("NOT currently supported", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI b")] // REP — no handler
    public void UnhandledCsiSequences_AreMarkedUnsupported(string seq)
    {
        Assert.Contains("NOT currently handled", VtTools.ExplainEscapeSequence(seq), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI E", "CNL")]
    [InlineData("CSI F", "CPL")]
    [InlineData("CSI G", "CHA")]
    public void SupportedCursorCapabilities_AreReportedFromContract(string sequence, string mnemonic)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains(mnemonic, result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("NOT currently handled", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported", result, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CSI ?2E", "CNL")]
    [InlineData("CSI ?2F", "CPL")]
    [InlineData("CSI 2$E", "CNL")]
    [InlineData("CSI 2$F", "CPL")]
    public void QualifiedContractForms_AreNotReportedAsTheSupportedBareCapability(string sequence, string mnemonic)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain(mnemonic, result, System.StringComparison.Ordinal);
        Assert.Contains("not in the curated table", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI 2;3E", "CNL")]
    [InlineData("CSI 2;3F", "CPL")]
    [InlineData("CSI 2;3G", "CHA")]
    [InlineData("CSI 2:3E", "CNL")]
    [InlineData("CSI 2:3F", "CPL")]
    [InlineData("CSI 2:3G", "CHA")]
    public void ParameterLists_ReportTheCapabilityAcceptedByTheParser(string sequence, string mnemonic)
    {
        Assert.Contains(mnemonic, VtTools.ExplainEscapeSequence(sequence), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// #274: the curated fallback table is keyed by final byte alone, so it used to describe
    /// XTSMGRAPHICS as SU and XTRMTITLE as SD. The parser ignores both, so those answers told the
    /// reader the opposite of what Ntilde does.
    /// </summary>
    [Theory]
    [InlineData("CSI >2T", "SD")]     // XTRMTITLE, not Scroll Down
    [InlineData("CSI <1P", "DCH")]
    [InlineData("CSI =1d", "VPA")]
    // Only leaders are covered here. ExplainEscapeSequence strips whitespace the user added
    // for readability ("CSI 2 J"), so a space intermediate cannot be expressed in its input
    // syntax at all - CSI Pn SP @ (SL) is indistinguishable from a legibly-typed ICH. That is
    // a deliberate trade in the tool's input handling, not something this change alters.
    public void QualifiedForms_AreNotExplainedAsTheBareFinalByte(string sequence, string bareName)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain(bareName, result, System.StringComparison.Ordinal);
        Assert.Contains("selects a different function", result, System.StringComparison.Ordinal);
    }

    /// <summary>XTSMGRAPHICS shares the final byte with SU but is a Sixel attribute query (#872).</summary>
    [Theory]
    [InlineData("CSI ?1;1;0S")]
    [InlineData("CSI ?2;1;0S")]
    public void Xtsmgraphics_is_explained_as_the_graphics_attribute_query(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains("XTSMGRAPHICS", result, System.StringComparison.Ordinal);
        Assert.Contains("4096 color registers", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Scroll", result, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A private-parameter byte outside the leader position, or a parameter byte after an
    /// intermediate, is malformed - the parser discards the whole sequence. The explainer has to
    /// say that rather than describe it as another function, which would be a different wrong
    /// answer to the same question.
    /// </summary>
    [Theory]
    [InlineData("CSI 1?2A")]
    [InlineData("CSI 1>2A")]
    [InlineData("CSI 2$3r")]
    public void MalformedQualifierPositions_AreReportedAsMalformed(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains("malformed", result, System.StringComparison.Ordinal);
        Assert.Contains("discards the whole sequence", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("CUU", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("DECSTBM", result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Parameter count selects a function too, and no leader or intermediate is involved:
    /// CSI 1;2;3;4;5 T is xterm highlight-mouse tracking, which the parser ignores, but it reads
    /// as a plain parameter list so the qualifier gate cannot see it. It used to be described
    /// as SD.
    /// </summary>
    [Fact]
    public void FiveParameterT_IsExplainedAsHighlightMouseTracking()
    {
        string result = VtTools.ExplainEscapeSequence("CSI 1;2;3;4;5T");

        Assert.DoesNotContain("Scroll Down Ps lines", result, System.StringComparison.Ordinal);
        Assert.Contains("highlight-mouse-tracking", result, System.StringComparison.Ordinal);
        Assert.Contains("ignores it", result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Two to four parameters on 'T' is neither SD (one parameter) nor highlight-mouse-tracking
    /// (exactly five). The parser ignores it, and the explainer must not pick either name -
    /// trading one wrong answer for another is not an improvement.
    /// </summary>
    [Theory]
    [InlineData("CSI 1;2T")]
    [InlineData("CSI 1;2;3T")]
    [InlineData("CSI 1;2;3;4T")]
    [InlineData("CSI 1:2T")]
    [InlineData("CSI 1:2:3T")]
    [InlineData("CSI 1;2:3T")]
    [InlineData("CSI 1:2:3:4:5T")]
    [InlineData("CSI 1;2;3:4;5T")]
    [InlineData("CSI 1;2;3;4;5;6T")]
    public void ParameterCountsMatchingNoDefinedForm_AreNotGivenOne(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain("Scroll Down Ps lines", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("highlight-mouse-tracking", result, System.StringComparison.Ordinal);
        Assert.Contains("matches no defined form", result, System.StringComparison.Ordinal);
    }

    /// <summary>The single-parameter and no-parameter spellings are still SD.</summary>
    [Theory]
    [InlineData("CSI T")]
    [InlineData("CSI 5T")]
    public void ScrollDown_StillResolves(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains("Scroll Down Ps lines", result, System.StringComparison.Ordinal);
    }

    /// <summary>The qualified forms that ARE defined must keep resolving.</summary>
    [Theory]
    [InlineData("CSI ?25h", "DECSET")]
    [InlineData("CSI ?1049l", "DECRST")]
    [InlineData("CSI >c", "DA")]
    [InlineData("CSI 6 q", "DECSCUSR")]
    [InlineData("CSI ?2J", "ED")]      // DECSED, still aliased to ED while DECSCA is unimplemented
    [InlineData("CSI ?6n", "DSR")]     // DECXCPR
    public void DefinedQualifiedForms_StillResolve(string sequence, string expected)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains(expected, result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// #274: a leader or an intermediate selects a different function, so the explainer must not
    /// describe these as CHA. It used to, reporting them as a "qualified form" that Ntilde
    /// processed as CHA - which mirrored the parser's missing leader guard. The parser ignores
    /// them now, so claiming CHA would send a reader looking for a cursor move that never happens.
    /// </summary>
    [Theory]
    [InlineData("CSI ?2G")]
    [InlineData("CSI 2$G")]
    public void QualifiedChaForms_AreNotExplainedAsCha(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain("CHA", result, System.StringComparison.Ordinal);
        Assert.Contains("not in the curated table", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI ?996n", "DSR color scheme")]
    [InlineData("ESC[?996n", "DSR color scheme")]
    [InlineData("DEC 2031", "Color-scheme reporting")]
    public void ColorSchemeCatalogForms_Resolve(string sequence, string mnemonic)
    {
        Assert.Contains(mnemonic, VtTools.ExplainEscapeSequence(sequence), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI 996n")]
    [InlineData("CSI >996n")]
    [InlineData("CSI ?996;1n")]
    [InlineData("CSI ?996:1n")]
    [InlineData("CSI ?996$n")]
    [InlineData("CSI ?997;1n")]
    public void OtherDsrForms_DoNotClaimColorSchemeQuery(string sequence)
    {
        Assert.DoesNotContain("DSR color scheme", VtTools.ExplainEscapeSequence(sequence), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI >4m", "XTMODKEYS modifyOtherKeys")]
    [InlineData("CSI >4;2m", "XTMODKEYS modifyOtherKeys")]
    [InlineData("ESC[>4;m", "XTMODKEYS modifyOtherKeys")]
    [InlineData("CSI >m", "XTMODKEYS modifyOtherKeys")]
    [InlineData("\x1b[?4m", "XTQMODKEYS modifyOtherKeys")]
    [InlineData("CSI ?1;4m", "XTQMODKEYS modifyOtherKeys")]
    public void XtermModifyOtherKeysForms_Resolve_AndAreNotSgr(string sequence, string mnemonic)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);
        Assert.Contains(mnemonic, result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SGR", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI >1;2m")]
    [InlineData("CSI ?1m")]
    [InlineData("CSI >4:2m")]
    [InlineData("CSI ?4:1m")]
    public void OtherKeyModifierForms_DoNotClaimModifyOtherKeysOrSgr(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);
        Assert.DoesNotContain("modifyOtherKeys —", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SGR —", result, System.StringComparison.Ordinal);
        Assert.Contains("ignore", result, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CSI =c")]
    [InlineData("CSI =0c")]
    [InlineData("ESC[=000c")]
    [InlineData("\x1b[=0c")]
    public void TertiaryDeviceAttributes_ExplainTheUnassignedNonuniqueIdentifier(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);
        Assert.Contains("DA3", result, System.StringComparison.Ordinal);
        Assert.Contains("unassigned", result, System.StringComparison.Ordinal);
        Assert.Contains("nonunique", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI =1c")]
    [InlineData("CSI =0;0c")]
    [InlineData("CSI =0:0c")]
    [InlineData("CSI =;c")]
    public void OtherTertiaryDeviceAttributeParameters_StayIgnoredInTheExplainer(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);
        Assert.DoesNotContain("nonunique", result, System.StringComparison.Ordinal);
        Assert.Contains("parser ignores", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DCS $qr")]
    [InlineData("ESC P$qrESC\\")]
    [InlineData("ESCP$qrESC\\")]
    [InlineData("\x1bP$qr\x1b\\")]
    [InlineData("\u0090$qr\u009c")]
    [InlineData("\\x1bP$qr\\x1b\\")]
    [InlineData("\\eP$qr\\e\\")]
    public void Exact_margin_query_explains_only_its_bounded_xterm_compatible_contract(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);
        Assert.Contains("DECRQSS top/bottom margins", result, StringComparison.Ordinal);
        Assert.Contains("standard ST", result, StringComparison.Ordinal);
        Assert.Contains("other settings are unavailable", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ESC P$qr")]
    [InlineData("ESCP$qr")]
    [InlineData("ESCP$qr\\x1b\\")]
    [InlineData("ESC P$qr\\e\\")]
    [InlineData("ESC P$qr\\x1b\\")]
    [InlineData("ESCP$qr\\e\\")]
    [InlineData("DCS $qm")]
    [InlineData("DCS $qr;")]
    [InlineData("\x1bP$qr\a")]
    [InlineData("\x1bP $qr\x1b\\")]
    [InlineData("\x1bP$qr \x1b\\")]
    [InlineData("\x1bP$qr")]
    [InlineData("\\x1bP$qr")]
    [InlineData("\\eP$qr")]
    [InlineData("\x1bP$qrESC\\")]
    [InlineData("\x1b P$qr\x1b\\")]
    [InlineData("\\x1b P$qr\\x1b\\")]
    [InlineData("\\x1bP$qrESC\\")]
    [InlineData("\x1bP$qr\\x1b\\")]
    [InlineData("\x1bP$qr\\e\\")]
    public void Other_DCS_payloads_or_incomplete_literal_frames_do_not_claim_a_margin_report(string sequence)
    {
        Assert.DoesNotContain("DECRQSS top/bottom margins", VtTools.ExplainEscapeSequence(sequence), StringComparison.Ordinal);
    }

    public static System.Collections.Generic.IEnumerable<object[]> MarginQueryRepresentationCases()
    {
        (string Prefix, string Kind)[] prefixes =
        [
            ("\x1bP", "raw"), ("\u0090", "raw"),
            ("\\x1bP", "escaped"), ("\\eP", "escaped"),
            ("ESC P", "textual"), ("ESCP", "textual"), ("DCS ", "abstract")
        ];
        (string Suffix, string Kind)[] suffixes =
        [
            ("\x1b\\", "raw"), ("\u009c", "raw"),
            ("\\x1b\\", "escaped"), ("\\e\\", "escaped"),
            ("ESC\\", "textual"), ("", "abstract"), ("\a", "recovery")
        ];
        foreach (var prefix in prefixes)
            foreach (var suffix in suffixes)
                yield return [prefix.Prefix + "$qr" + suffix.Suffix, prefix.Kind == suffix.Kind];
    }

    [Theory]
    [MemberData(nameof(MarginQueryRepresentationCases))]
    public void Margin_query_prefix_and_terminator_representations_must_agree(string sequence, bool supported)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);
        Assert.Equal(supported, result.Contains("DECRQSS top/bottom margins", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DCS $qm", "DECRQSS SGR report")]
    [InlineData("\x1bP$qm\x1b\\", "DECRQSS SGR report")]
    [InlineData("\u0090$qm\u009c", "DECRQSS SGR report")]
    [InlineData("\\eP$qm\\e\\", "DECRQSS SGR report")]
    [InlineData("ESC P$qmESC\\", "DECRQSS SGR report")]
    [InlineData("DCS $q q", "DECRQSS cursor style report")]
    [InlineData("\x1bP$q q\x1b\\", "DECRQSS cursor style report")]
    [InlineData("\\x1bP$q q\\x1b\\", "DECRQSS cursor style report")]
    [InlineData("ESCP$q qESC\\", "DECRQSS cursor style report")]
    public void Exact_sgr_and_cursor_style_queries_explain_their_reports(string sequence, string mnemonic)
    {
        Assert.Contains(mnemonic, VtTools.ExplainEscapeSequence(sequence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DCS $qm;")]
    [InlineData("DCS $q  q")]
    [InlineData("DCS $qq")]
    [InlineData("\x1bP$qm\a")]
    [InlineData("\x1bP$q q")]
    [InlineData("ESC P$qm")]
    [InlineData("\x1bP$q q\\e\\")]
    public void Inexact_sgr_or_cursor_style_queries_claim_no_report(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);
        Assert.DoesNotContain("DECRQSS SGR report", result, StringComparison.Ordinal);
        Assert.DoesNotContain("DECRQSS cursor style report", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\x1bP+q436f\x1b\\")]
    [InlineData("\u0090+q436f;5463\u009c")]
    [InlineData("\\x1bP+q436f\\x1b\\")]
    [InlineData("\\eP+q544e\\e\\")]
    [InlineData("ESC P+q436fESC\\")]
    [InlineData("DCS +q")]
    public void Capability_queries_explain_the_xtgettcap_report(string sequence)
    {
        Assert.Contains("XTGETTCAP capability report", VtTools.ExplainEscapeSequence(sequence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\x1bP+q436f\a")]
    [InlineData("\x1bP+q436f")]
    [InlineData("ESC P+q436f")]
    [InlineData("\x1bP+q436f\\e\\")]
    public void Unterminated_or_mixed_capability_queries_claim_no_report(string sequence)
    {
        Assert.DoesNotContain("XTGETTCAP", VtTools.ExplainEscapeSequence(sequence), StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogCapabilities_AgreeWithExplainerSupportClaims()
    {
        foreach (VtCapability capability in VtCapabilityCatalog.All)
        {
            string result = VtTools.ExplainEscapeSequence(capability.Key.Replace(':', ' '));

            Assert.Contains(capability.Mnemonic, result, System.StringComparison.Ordinal);
            if (capability.Support == VtSupport.Supported)
            {
                Assert.DoesNotContain("NOT currently handled", result, System.StringComparison.Ordinal);
                Assert.DoesNotContain("unsupported", result, System.StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void CsiIntermediateBytes_AreSurfaced()
    {
        // A real (punctuation) intermediate byte must be surfaced, e.g. DECSTR `CSI ! p`.
        var result = VtTools.ExplainEscapeSequence("CSI ! p");
        Assert.Contains("intermediate", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("!", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingSpaces_AreNotTreatedAsIntermediates()
    {
        // "CSI 2 J" has only a formatting space; it must resolve to ED with no intermediate note.
        var result = VtTools.ExplainEscapeSequence("CSI 2 J");
        Assert.Contains("ED", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("intermediate byte", result, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownCsiFinalByte_IsReportedGracefully()
    {
        var result = VtTools.ExplainEscapeSequence("CSI 1 ~");
        Assert.Contains("final byte", result, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_PromptsForInput()
    {
        Assert.Contains("Provide a sequence", VtTools.ExplainEscapeSequence(""));
    }

    [Fact]
    public void Garbage_IsRejectedGracefully()
    {
        Assert.Contains("Unrecognized", VtTools.ExplainEscapeSequence("hello world"));
    }
}

public class VtTestPlanTests
{
    [Fact]
    public void TestPlan_IncludesKeySectionsAndFeature()
    {
        var plan = VtTools.GenerateVtTestPlan("OSC 8 hyperlinks");
        Assert.Contains("OSC 8 hyperlinks", plan, System.StringComparison.Ordinal);
        Assert.Contains("## Cases to cover", plan, System.StringComparison.Ordinal);
        Assert.Contains("## Where tests live", plan, System.StringComparison.Ordinal);
        Assert.Contains("## Verification", plan, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TestPlan_NullFeature_DoesNotThrow()
    {
        Assert.Contains("VT test plan", VtTools.GenerateVtTestPlan(null!));
    }
}

public class SuggestRelevantFilesTests
{
    [Theory]
    [InlineData("reflow edge cases", "TerminalBuffer.ReflowEngine.cs")]
    [InlineData("glyph atlas overflow", "GlyphAtlas.cs")]
    [InlineData("ssh key auth", "TerminalProfile.cs")]
    [InlineData("OSC parser sequence", "AnsiParser.cs")]
    [InlineData("theme validation", "src/Ntilde.App/Shell/ThemeManager.cs")] // correct path
    public void MapsTopicToFiles(string topic, string expectedFile)
    {
        Assert.Contains(expectedFile, WorkflowTools.SuggestRelevantFiles(topic), System.StringComparison.Ordinal);
    }

    [Fact]
    public void UnmappedTopic_FallsBackToArchitectureGuidance()
    {
        Assert.Contains("get_architecture_map", WorkflowTools.SuggestRelevantFiles("the gizmo widget"));
    }

    [Fact]
    public void NullTopic_DoesNotThrow()
    {
        // No keyword match → fallback guidance; must not throw.
        Assert.Contains("architecture", WorkflowTools.SuggestRelevantFiles(null!), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParserTopic_ListsEveryFileOfTheParser()
    {
        // AnsiParser is split into partial files by sequence family, so a DCS or APC topic has to reach
        // AnsiParser.Images.cs or AnsiParser.KittyGraphics.cs, not just AnsiParser.cs.
        string root = RepoRoot();
        var parts = Directory.GetFiles(Path.Combine(root, "src", "Ntilde.VT"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bpartial\s+class\s+AnsiParser\b"))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();
        Assert.True(parts.Count > 1, "Expected AnsiParser to span several partial files.");

        string hints = WorkflowTools.SuggestRelevantFiles("parser");
        foreach (var part in parts)
        {
            Assert.Contains(part, hints, System.StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate 'Ntilde.sln' above the test output directory.");
    }
}
