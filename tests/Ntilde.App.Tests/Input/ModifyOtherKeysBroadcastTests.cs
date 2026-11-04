using System.Linq;
using Avalonia.Input;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input;

// Issue #829: tab broadcast encodes each key for the receiving pane's own modes. One key fanned
// out to a legacy pane, a level-1 pane, a level-2 pane and a kitty pane (which keeps legacy
// bytes: kitty wins over modifyOtherKeys there and broadcast does not speak kitty).
public sealed class ModifyOtherKeysBroadcastTests
{
    private static TerminalBuffer Pane(string negotiation)
    {
        var buffer = new TerminalBuffer(80, 24);
        new AnsiParser(buffer).Process(negotiation);
        return buffer;
    }

    private static readonly TerminalBuffer[] Panes =
    [
        Pane(""),                          // legacy
        Pane("\x1b[>4;1m"),                // modifyOtherKeys 1 (Emacs)
        Pane("\x1b[?1049h\x1b[>4;2m"),     // modifyOtherKeys 2 on the alternate screen (Vim)
        Pane("\x1b[>4;2m\x1b[>1u"),        // kitty disambiguate over a negotiated level
    ];

    private static string?[] Fanout(Key key, KeyModifiers modifiers)
        => Panes.Select(buffer => MainWindow.TryMapBroadcastKey(key, modifiers, PhysicalKey.None, buffer, out string? sequence)
            ? sequence : null).ToArray();

    [Fact]
    public void CtrlLetter_IsC0_ForLegacyLevelOneAndKitty_AndCsi27_ForLevelTwo()
        => Assert.Equal(new[] { "\x01", "\x01", "\x1b[27;5;97~", "\x01" }, Fanout(Key.A, KeyModifiers.Control));

    [Fact]
    public void CtrlDigit_IsEncodedFromLevelOne()
        => Assert.Equal(new[] { null, "\x1b[27;5;49~", "\x1b[27;5;49~", null }, Fanout(Key.D1, KeyModifiers.Control));

    [Fact]
    public void ModifiedEnter_PerReceiver()
        => Assert.Equal(new[] { "\r", "\x1b[27;2;13~", "\x1b[27;2;13~", "\r" }, Fanout(Key.Enter, KeyModifiers.Shift));

    [Fact]
    public void CtrlShiftLetter_OnlyReachesTheLevelTwoPane()
        => Assert.Equal(new[] { null, null, "\x1b[27;6;65~", null }, Fanout(Key.A, KeyModifiers.Control | KeyModifiers.Shift));

    [Fact]
    public void ModifiedBackspaceAndEscape_PerReceiver()
    {
        Assert.Equal(new[] { "\x7f", "\x7f", "\x1b[27;2;127~", "\x7f" }, Fanout(Key.Back, KeyModifiers.Shift));
        Assert.Equal(new[] { "\x1b", "\x1b", "\x1b[27;5;27~", "\x1b" }, Fanout(Key.Escape, KeyModifiers.Control));
    }

    [Fact]
    public void TextProducingChords_AreLeftToTheTextBroadcast()
    {
        // Shift+Space produces TextInput, which every sibling already receives as text;
        // encoding it here too would deliver it twice to the level-2 pane.
        Assert.All(Fanout(Key.Space, KeyModifiers.Shift), Assert.Null);
        Assert.All(Fanout(Key.A, KeyModifiers.Shift), Assert.Null);
        // Alt chords stay unbroadcast at every level, as before.
        Assert.All(Fanout(Key.A, KeyModifiers.Alt), Assert.Null);
    }

    [Fact]
    public void UnmodifiedAndSpecialKeys_AreUnchanged()
    {
        Assert.Equal(new[] { "\r", "\r", "\r", "\r" }, Fanout(Key.Enter, KeyModifiers.None));
        Assert.Equal(new[] { "\x1b[1;5D", "\x1b[1;5D", "\x1b[1;5D", "\x1b[1;5D" }, Fanout(Key.Left, KeyModifiers.Control));
    }

    [Fact]
    public void CursorKeys_FollowTheReceiversOwnDeccKm()
    {
        TerminalBuffer application = Pane("\x1b[?1h");
        Assert.True(MainWindow.TryMapBroadcastKey(Key.Up, KeyModifiers.None, PhysicalKey.None, application, out string? up));
        Assert.Equal("\x1bOA", up);
        Assert.True(MainWindow.TryMapBroadcastKey(Key.Up, KeyModifiers.None, PhysicalKey.None, Panes[0], out up));
        Assert.Equal("\x1b[A", up);
    }
}
