namespace Ntilde.VT;

public partial class AnsiParser
{
    /// <summary>
    /// The iTerm2 feature-reporting declaration (<c>docs/qa/terminfo/iterm-feature-reporting.md</c>): every
    /// code below names a capability this VT implements with owning tests, in the specification's code-table
    /// order. Codes without implementation evidence stay out: <c>Aw</c> (ambiguous-width rendering) and
    /// <c>Uw</c> (a claimed Unicode width-table version) have none, and <c>Sx</c> (SIXEL) is measured
    /// unsupported — see the retained raster-fidelity failure. <c>F</c> is the specification's code both for
    /// focus reporting and for the OSC 1337 file/image protocol; this terminal implements both, so one
    /// <c>F</c> covers them. <c>Cw</c> (clipboard writing) is answered only while the host accepts OSC 52
    /// writes — the same gate the XTGETTCAP <c>Ms</c> entry uses — so headless, persistent and
    /// setting-disabled sessions omit it rather than advertise what they cannot do.
    /// </summary>
    /// <remarks>
    /// Codes and owning evidence, in specification order: <c>T3</c> 24-bit color (compatibility
    /// <c>38;2;r;g;b</c> and full colon form, <see cref="AnsiParser.Sgr"/>); <c>Cw</c> OSC 52 clipboard
    /// writing (<see cref="ClipboardWriteAvailable"/>); <c>Lr</c> DECSLRM margins
    /// (<c>CSI Pl ; Pr s</c> under DECLRMM); <c>M</c> mouse reporting (DECSET 1000/1002/1003 with the
    /// required SGR 1006); <c>Sc7</c> DECSCUSR modes 1–4, 5–6 and mode-0 reset; <c>U</c> basic Unicode;
    /// <c>Ts3</c> title setting (OSC 0/1/2) and title stacks (CSI 22/23 t); <c>B</c> bracketed paste
    /// (DECSET 2004 with paste-terminator sanitization); <c>F</c> focus reporting (DECSET 1004 answered
    /// with CSI I/CSI O) and OSC 1337 inline images; <c>Gs</c> strikethrough (SGR 9); <c>Go</c> overline
    /// (SGR 53); <c>Sy</c> synchronized output (DECSET 2026); <c>H</c> OSC 8 hyperlinks; <c>No</c> OSC 9
    /// notifications; <c>P</c> OSC 9;4 progress.
    /// </remarks>
    internal const string CapabilityFeatures = "T3LrMSc7UTs3BFGsGoSyHNoP";

    /// <summary>The declaration above with <c>Cw</c> in its specification position: the host currently accepts OSC 52 writes.</summary>
    internal const string CapabilityFeaturesWithClipboardWrite = "T3CwLrMSc7UTs3BFGsGoSyHNoP";

    /// <summary>
    /// Routes the iTerm2 feature-reporting query: the exact payload <c>1337;RequestCapabilities</c> is
    /// answered with one <c>OSC 1337 ; Capabilities = {FeatureString} ST</c> reply, always ST-terminated
    /// as the specification writes the response, whatever terminator the request used. The reply changes no
    /// screen, cursor or mode state. Any other payload — including a bare <c>1337;Capabilities</c>, an
    /// unsolicited <c>Capabilities=</c> assignment or any other case — is not a request and stays with the
    /// existing routing (the image and cell-size forms keep their handlers; everything else is ignored
    /// without a reply).
    /// </summary>
    private bool HandleCapabilitiesRequest(string osc)
    {
        if (osc != "1337;RequestCapabilities") return false;
        string features = ClipboardWriteAvailable?.Invoke() == true
            ? CapabilityFeaturesWithClipboardWrite
            : CapabilityFeatures;
        OnResponse?.Invoke($"\x1b]1337;Capabilities={features}\x1b\\");
        return true;
    }
}
