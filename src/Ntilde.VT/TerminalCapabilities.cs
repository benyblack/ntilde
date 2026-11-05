namespace Ntilde.VT;

/// <summary>The terminfo type of a <see cref="TerminalCapability"/>.</summary>
public enum TerminalCapabilityKind
{
    /// <summary>A flag such as <c>Tc</c>: present or absent, with no value.</summary>
    Flag,
    /// <summary>A number such as <c>colors#256</c>; the value is its decimal text.</summary>
    Number,
    /// <summary>A control string such as <c>Smulx</c>, possibly with terminfo <c>%</c> parameters.</summary>
    Text,
}

/// <summary>One capability Ntilde advertises beyond <c>xterm-256color</c>.</summary>
/// <param name="Name">The terminfo name, case-sensitive (<c>Co</c> and <c>co</c> differ).</param>
/// <param name="Kind">Boolean, numeric or string.</param>
/// <param name="Value">
/// Empty for a boolean, the decimal text for a number, and for a string the compiled bytes a
/// program sends: real control characters (ESC, BEL), with terminfo <c>%</c> parameter
/// operations left unexpanded, as <c>tigetstr</c> returns it.
/// </param>
/// <param name="Evidence">Repository-relative test files that pin the behaviour the value relies on.</param>
public sealed record TerminalCapability(string Name, TerminalCapabilityKind Kind, string Value, IReadOnlyList<string> Evidence)
{
    /// <summary>
    /// True when this capability is offered only while the presenting host says the feature is
    /// available (see <see cref="AnsiParser.ClipboardWriteAvailable"/>).
    /// </summary>
    public bool RequiresClipboardWrite { get; init; }
}

/// <summary>
/// The single source of truth for what Ntilde advertises beyond <c>xterm-256color</c> (#844).
/// XTGETTCAP answers from this table, and the opt-in <c>xterm-ntilde</c> terminfo entry is built
/// from it, so the two cannot disagree. Capabilities inherited from <c>xterm-256color</c> are not
/// listed and XTGETTCAP does not report them. An entry belongs here only when a test pins the
/// behaviour its value relies on; <c>TerminalCapabilityTests</c> also runs every string value
/// through the parser.
/// </summary>
public static class TerminalCapabilities
{
    private const string Sgr = "tests/Ntilde.App.Tests/SgrAttributeTests.cs";
    private const string Underline = "tests/Ntilde.VT.Tests/UnderlineStyleTests.cs";
    private const string Owner = "tests/Ntilde.VT.Tests/TerminalCapabilityTests.cs";

    /// <summary>Every advertised capability, in a stable order.</summary>
    public static IReadOnlyList<TerminalCapability> All { get; } =
    [
        new("Co", TerminalCapabilityKind.Number, "256", [Owner, Sgr]),
        new("colors", TerminalCapabilityKind.Number, "256", [Owner, Sgr]),
        new("RGB", TerminalCapabilityKind.Flag, "", [Owner, Sgr]),
        new("Tc", TerminalCapabilityKind.Flag, "", [Owner, Sgr]),
        new("setrgbf", TerminalCapabilityKind.Text, "\u001b[38;2;%p1%d;%p2%d;%p3%dm", [Owner, Sgr]),
        new("setrgbb", TerminalCapabilityKind.Text, "\u001b[48;2;%p1%d;%p2%d;%p3%dm", [Owner, Sgr]),
        new("Su", TerminalCapabilityKind.Flag, "", [Owner, Underline]),
        new("Smulx", TerminalCapabilityKind.Text, "\u001b[4:%p1%dm", [Owner, Underline]),
        new("Setulc", TerminalCapabilityKind.Text,
            "\u001b[58:2::%p1%{65536}%/%d:%p1%{256}%/%{255}%&%d:%p1%{255}%&%dm", [Owner, Underline]),
        new("Sync", TerminalCapabilityKind.Text, "\u001b[?2026%?%p1%{1}%-%tl%eh%;",
            [Owner, "tests/Ntilde.VT.Tests/DecrqmTests.cs", "tests/Ntilde.App.Tests/Core/SynchronizedRenderingTests.cs"]),
        new("Ss", TerminalCapabilityKind.Text, "\u001b[%p1%d q",
            [Owner, "tests/Ntilde.VT.Tests/CursorStyleInvalidationTests.cs"]),
        new("Se", TerminalCapabilityKind.Text, "\u001b[0 q",
            [Owner, "tests/Ntilde.VT.Tests/DecrqssSettingReportTests.cs"]),
        new("Ms", TerminalCapabilityKind.Text, "\u001b]52;%p1%s;%p2%s\u0007",
            [Owner, "tests/Ntilde.App.Tests/OscUxTests.cs", "tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs"])
        {
            RequiresClipboardWrite = true,
        },
        new("tsl", TerminalCapabilityKind.Text, "\u001b]2;", [Owner, "tests/Ntilde.VT.Tests/OscTitleTests.cs"]),
        new("fsl", TerminalCapabilityKind.Text, "\u0007", [Owner, "tests/Ntilde.VT.Tests/OscTitleTests.cs"]),
    ];

    private static readonly Dictionary<string, TerminalCapability> ByName =
        All.ToDictionary(capability => capability.Name, StringComparer.Ordinal);

    /// <summary>Looks a capability up by its exact, case-sensitive terminfo name.</summary>
    public static bool TryGet(string name, out TerminalCapability capability)
        => ByName.TryGetValue(name, out capability!);
}
