using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class KittyAssociatedTextRoutingTests
{
    private sealed class KeyView : TerminalView
    {
        public KeyEventArgs Down(Key key, KeyModifiers modifiers = KeyModifiers.None, PhysicalKey physical = PhysicalKey.None, string? symbol = null)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, KeySymbol = symbol, RoutedEvent = KeyDownEvent };
            OnKeyDown(e);
            return e;
        }
        public KeyEventArgs Up(Key key, KeyModifiers modifiers = KeyModifiers.None, PhysicalKey physical = PhysicalKey.None)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, RoutedEvent = KeyUpEvent };
            OnKeyUp(e);
            return e;
        }
        public void Text(string text) => OnTextInput(new TextInputEventArgs { Text = text, RoutedEvent = TextInputEvent });
        public void LoseFocus() => OnLostFocus(new FocusChangedEventArgs(LostFocusEvent));
    }

    private static (KeyView View, TerminalBuffer Buffer, List<string> Sent) Create(int flags = 24)
    {
        var sent = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
        var buffer = new TerminalBuffer(80, 24);
        buffer.Modes.KittyKeyboard.Push(flags);
        var view = new KeyView();
        view.SetBuffer(buffer);
        view.SetSession(session.Object);
        return (view, buffer, sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.A, KeyModifiers.None, PhysicalKey.A, "a", "a", "\u001b[97;1;97u")]
    [InlineData(Key.A, KeyModifiers.Shift, PhysicalKey.A, "A", "A", "\u001b[97;2;65u")]
    [InlineData(Key.A, KeyModifiers.None, PhysicalKey.A, "a", "á", "\u001b[97;1;225u")]
    [InlineData(Key.A, KeyModifiers.Alt, PhysicalKey.A, "å", "å", "\u001b[97;3;229u")]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, PhysicalKey.Q, "@", "@", "\u001b[113;7;64u")]
    [InlineData(Key.NumPad1, KeyModifiers.None, PhysicalKey.NumPad1, "1", "1", "\u001b[57400;1;49u")]
    [InlineData(Key.None, KeyModifiers.None, PhysicalKey.None, "あ", "あ", "\u001b[12354;1;12354u")]
    public void Actual_text_callback_completes_one_key_frame(Key key, KeyModifiers modifiers, PhysicalKey physical, string symbol, string text, string expected)
    {
        var (view, _, sent) = Create();
        Assert.False(view.Down(key, modifiers, physical, symbol).Handled); // Enables native text on Win32, X11 and macOS.
        Assert.Empty(sent);
        view.Text(text);
        Assert.Equal(expected, Assert.Single(sent));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Single(sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.None)]
    [InlineData(Key.ImeProcessed)]
    [InlineData(Key.DeadCharProcessed)]
    public void Text_only_composition_has_zero_key_and_preserves_all_scalars(Key key)
    {
        var (view, _, sent) = Create();
        Assert.False(view.Down(key).Handled);
        view.Text("e\u0301😀");
        Assert.Equal("\u001b[0;1;101:769:128512u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Repeat_keeps_pressed_primary_but_uses_actual_current_text_and_release_has_no_payload()
    {
        var (view, _, sent) = Create(26);
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        view.Text("a");
        view.Down(Key.Y, KeyModifiers.Shift, PhysicalKey.A, "Y");
        view.Text("Y");
        Assert.True(view.Up(Key.Y, physical: PhysicalKey.A).Handled);
        Assert.Equal(new[] { "\u001b[97;1;97u", "\u001b[97;2:2;89u", "\u001b[97;1:3u" }, sent);
    }

    [AvaloniaFact]
    public void Alternate_fields_and_text_fields_are_independent()
    {
        var (view, _, sent) = Create(31);
        view.Down(Key.C, KeyModifiers.Shift, PhysicalKey.C, "С");
        view.Text("С");
        view.Up(Key.C, physical: PhysicalKey.C);
        Assert.Equal(new[] { "\u001b[1089:1057:99;2;1057u", "\u001b[1089::99;1:3u" }, sent);
    }

    [AvaloniaFact]
    public void Captured_associated_text_survives_capability_downgrade_without_a_new_stack_release()
    {
        var (view, buffer, sent) = Create(26);
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        buffer.Modes.KittyKeyboard.SetSupportedFlags(7);
        view.Text("a");
        Assert.False(view.Up(Key.A, physical: PhysicalKey.A).Handled);
        Assert.Equal("\u001b[97;1;97u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Captured_legacy_text_remains_raw_after_capability_recovery()
    {
        var (view, buffer, sent) = Create();
        buffer.Modes.KittyKeyboard.SetSupportedFlags(7);
        view.Down(Key.A, symbol: "a");
        buffer.Modes.KittyKeyboard.SetSupportedFlags(31);
        view.Text("a");
        Assert.Equal("a", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Flag_eight_without_text_does_not_gain_a_payload_during_a_policy_flip()
    {
        var (view, buffer, sent) = Create(8);
        Assert.True(view.Down(Key.A, symbol: "a").Handled);
        buffer.Modes.KittyKeyboard.Push(24);
        view.Text("a");
        Assert.Equal("\u001b[97u", Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(17)]
    public void Without_flag_eight_text_keeps_legacy_encoding(int flags)
    {
        var (view, _, sent) = Create(flags);
        Assert.False(view.Down(Key.A, symbol: "a").Handled);
        view.Text("a");
        Assert.Equal("a", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Matching_release_flushes_the_press_before_the_release()
    {
        var (view, _, sent) = Create(26);
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        Assert.Empty(sent);
        Assert.True(view.Up(Key.A, physical: PhysicalKey.A).Handled);
        Assert.Equal(new[] { "\u001b[97u", "\u001b[97;1:3u" }, sent);
    }

    [AvaloniaFact]
    public void Dispatcher_fallback_is_once_and_late_text_has_no_repeated_key_identity()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        Assert.Empty(sent);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal("\u001b[97u", Assert.Single(sent));
        view.Text("a");
        Assert.Equal(new[] { "\u001b[97u", "\u001b[0;1;97u" }, sent);
    }

    [AvaloniaFact]
    public void Next_non_text_key_flushes_the_prior_frame_in_order()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.Down(Key.LeftShift, KeyModifiers.Shift, PhysicalKey.ShiftLeft);
        Assert.Equal(new[] { "\u001b[97u", "\u001b[57441;2u" }, sent);
    }

    [AvaloniaTheory]
    [InlineData("\u001b[31m")]
    [InlineData("a\u0085")]
    public void Invalid_text_does_not_inject_bytes_but_a_known_press_is_retained(string text)
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.Text(text);
        Assert.Equal("\u001b[97u", Assert.Single(sent));
        view.Text(text);
        Assert.Single(sent);
    }

    [AvaloniaTheory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void Runtime_malformed_text_is_rejected_without_substituting_unicode_characters(int codeUnit)
    {
        string text = new((char)codeUnit, 1);
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.Text(text);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal("\u001b[97u", Assert.Single(sent));
        view.Text(text);
        Assert.Single(sent);
    }

    [AvaloniaFact]
    public void Split_utf16_callbacks_complete_one_scalar_in_the_waiting_key_frame()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.Text(new string((char)0xd83d, 1));
        Assert.Empty(sent);
        view.Text(new string((char)0xde00, 1));
        Assert.Equal("\u001b[97;1;128512u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Split_text_only_callbacks_preserve_supplementary_unicode()
    {
        var (view, _, sent) = Create();
        view.Text(new string((char)0xd83d, 1));
        Assert.Empty(sent);
        view.Text(new string((char)0xde00, 1));
        Assert.Equal("\u001b[0;1;128512u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Unicode_packet_key_events_between_units_do_not_break_the_scalar()
    {
        var (view, _, sent) = Create();
        string high = new((char)0xd83d, 1), low = new((char)0xde00, 1);
        view.Down(Key.None, symbol: high);
        view.Text(high);
        view.Up(Key.None);
        view.Down(Key.None, symbol: low);
        view.Text(low);
        view.Up(Key.None);
        Assert.Equal("\u001b[0;1;128512u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Split_scalar_after_key_fallback_becomes_one_zero_key_text_event()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.Text(new string((char)0xd83d, 1));
        Assert.Empty(sent);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        view.Text(new string((char)0xde00, 1));
        Assert.Equal(new[] { "\u001b[97u", "\u001b[0;1;128512u" }, sent);
    }

    [AvaloniaFact]
    public void Split_scalar_keeps_the_mode_in_which_its_first_unit_arrived()
    {
        var (view, buffer, sent) = Create();
        view.Text(new string((char)0xd83d, 1));
        buffer.Modes.KittyKeyboard.SetSupportedFlags(7);
        view.Text(new string((char)0xde00, 1));
        Assert.Equal("\u001b[0;1;128512u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Unmatched_high_unit_is_discarded_without_losing_the_following_valid_text()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.Text(new string((char)0xd83d, 1));
        Assert.Empty(sent);
        view.Text("b");
        Assert.Equal("\u001b[97;1;98u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Context_reset_discards_an_incomplete_unicode_fragment()
    {
        var (view, _, sent) = Create();
        view.Text(new string((char)0xd83d, 1));
        view.LoseFocus();
        view.Text(new string((char)0xde00, 1));
        Assert.Empty(sent);
        view.Text("b");
        Assert.Equal("\u001b[0;1;98u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void New_known_key_expires_an_old_fragment_without_losing_its_own_text_association()
    {
        var (view, _, sent) = Create();
        view.Text(new string((char)0xd83d, 1));
        view.Down(Key.B, symbol: "b");
        view.Text("b");
        Assert.Equal("\u001b[98;1;98u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Focus_reset_discards_unsent_old_input_without_swallowing_new_text()
    {
        var (view, buffer, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.LoseFocus();
        buffer.Modes.KittyKeyboard.SetSupportedFlags(7);
        view.Text("かな");
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal("かな", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Same_flag_stack_replacement_cannot_acquire_a_release_for_an_old_deferred_press()
    {
        var (view, buffer, sent) = Create(26);
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        buffer.Modes.KittyKeyboard.Pop(1);
        buffer.Modes.KittyKeyboard.Push(26);
        view.Text("a");
        Assert.False(view.Up(Key.A, physical: PhysicalKey.A).Handled);
        Assert.Equal("\u001b[97;1;97u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Original_text_mode_survives_protocol_disablement_until_the_callback()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, symbol: "a");
        view.ApplySettings(new TerminalSettings { EnableKittyKeyboardProtocol = false });
        view.Text("a");
        Assert.Equal("\u001b[97;1;97u", Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData("buffer")]
    [InlineData("session")]
    public void Context_replacement_drops_unsent_key_but_admits_new_text(string context)
    {
        var (view, buffer, sent) = Create();
        view.Down(Key.A, symbol: "a");
        if (context == "buffer") view.SetBuffer(new TerminalBuffer(80, 24));
        else
        {
            var session = new Mock<ITerminalSession>();
            session.SetupGet(x => x.IsProcessRunning).Returns(true);
            session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
            view.SetSession(session.Object);
            buffer.Modes.KittyKeyboard.SetSupportedFlags(7);
        }
        view.Text("かな");
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal("かな", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Typeahead_fallback_preserves_order_and_does_not_post_duplicate_frames()
    {
        var (view, _, sent) = Create();
        for (int i = 0; i < 20; i++) view.Down(Key.A, symbol: "a");
        Assert.Equal(19, sent.Count);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal(Enumerable.Repeat("\u001b[97u", 20), sent);
    }

    [AvaloniaFact]
    public void Reentrant_text_callback_does_not_reuse_a_consumed_pending_key()
    {
        var (view, _, sent) = Create();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(frame =>
        {
            sent.Add(frame);
            if (frame == "\u001b[97;1;97u") view.Text("b");
        });
        view.SetSession(session.Object);
        view.Down(Key.A, symbol: "a");
        view.Text("a");
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal(new[] { "\u001b[97;1;97u", "\u001b[0;1;98u" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(4)]
    public void Reentrant_keydowns_during_fallback_are_forwarded_before_the_outer_key(int nestedCount)
    {
        var (view, _, sent) = Create();
        int remaining = nestedCount;
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(frame =>
        {
            sent.Add(frame);
            if (frame.EndsWith('u') && !frame.Contains(';') && remaining-- > 0)
                view.Down(Key.B, physical: PhysicalKey.B, symbol: "b");
        });
        view.SetSession(session.Object);
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        Assert.False(view.Down(Key.C, physical: PhysicalKey.C, symbol: "c").Handled);
        view.Text("c");
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal(new[] { "\u001b[97u" }.Concat(Enumerable.Repeat("\u001b[98u", nestedCount))
            .Append("\u001b[99;1;99u"), sent);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reentrant_text_fragment_obeys_the_outer_key_identity(bool knownKey)
    {
        var (view, _, sent) = Create();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(frame =>
        {
            sent.Add(frame);
            if (frame == "\u001b[97u") view.Text(new string((char)0xd83d, 1));
        });
        view.SetSession(session.Object);
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        view.Down(knownKey ? Key.C : Key.None, symbol: knownKey ? "c" : null);
        view.Text(new string((char)0xde00, 1));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal(new[] { "\u001b[97u", knownKey ? "\u001b[99u" : "\u001b[0;1;128512u" }, sent);
    }

    [AvaloniaFact]
    public void Unrelated_release_leaves_the_waiting_text_key_associated()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        view.Up(Key.B, physical: PhysicalKey.B);
        Assert.Empty(sent);
        view.Text("a");
        Assert.Equal("\u001b[97;1;97u", Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Canonical_text_notifies_the_typing_observer_once_with_actual_payload(bool knownKey)
    {
        var (view, _, sent) = Create(26);
        var observed = new List<string>();
        view.TextInputObserved += text =>
        {
            Assert.NotEmpty(sent); // Same ordering as the legacy text path.
            observed.Add(text);
        };
        if (knownKey) view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        view.Text("e\u0301😀");
        view.Up(Key.A, physical: PhysicalKey.A);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Equal("e\u0301😀", Assert.Single(observed));
        Assert.StartsWith(knownKey ? "\u001b[97;1;" : "\u001b[0;1;", sent[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Split_scalar_notifies_the_typing_observer_once_after_completion()
    {
        var (view, _, _) = Create();
        var observed = new List<string>();
        view.TextInputObserved += observed.Add;
        view.Text(new string((char)0xd83d, 1));
        Assert.Empty(observed);
        view.Text(new string((char)0xde00, 1));
        Assert.Equal("😀", Assert.Single(observed));
    }

    [AvaloniaFact]
    public void Fallback_and_rejected_payload_do_not_notify_but_late_valid_text_does()
    {
        var (view, _, _) = Create(26);
        var observed = new List<string>();
        view.TextInputObserved += observed.Add;
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        view.Text("\u001b[31m");
        view.Down(Key.B, physical: PhysicalKey.B, symbol: "b");
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Assert.Empty(observed);
        view.Text("b");
        view.Up(Key.B, physical: PhysicalKey.B);
        Assert.Equal("b", Assert.Single(observed));
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, "\u001b[13u")]
    [InlineData(Key.Tab, "\u001b[9u")]
    [InlineData(Key.Back, "\u001b[127u")]
    [InlineData(Key.Escape, "\u001b[27u")]
    public void Recovery_keys_never_wait_for_forbidden_control_text(Key key, string expected)
    {
        var (view, _, sent) = Create();
        Assert.True(view.Down(key).Handled);
        Assert.Equal(expected, Assert.Single(sent));
    }
}
