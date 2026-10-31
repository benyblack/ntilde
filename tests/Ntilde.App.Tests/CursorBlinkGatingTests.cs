using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// Guards the #126 gating rule: the cursor-blink timer must only run when the cursor is
    /// actually drawn. Render() hides the cursor whenever the view lacks keyboard focus, so
    /// a blink tick on an unfocused pane produced a pixel-identical frame - a full render
    /// pass every 530 ms, per pane, indefinitely.
    ///
    /// These pure tests pin the focus/effective-policy gate. TerminalViewCursorBlinkTests
    /// additionally drives attached, focused views and timer callbacks, checking application
    /// overrides, host defaults and lifecycle changes without waiting on wall-clock ticks.
    /// </summary>
    public class CursorBlinkGatingTests
    {
        [Theory]
        // Effective blink on, focused: the only combination that should tick.
        [InlineData(true, true, true)]
        // Focused but the effective policy selects a steady cursor.
        [InlineData(true, false, false)]
        // The #126 case: blink enabled, pane unfocused - cursor is not drawn, so no ticks.
        [InlineData(false, true, false)]
        // Neither.
        [InlineData(false, false, false)]
        public void BlinkTimerRunsOnlyWhenFocusedAndEnabled(bool focused, bool blinkEnabled, bool expected)
        {
            Assert.Equal(expected, TerminalView.ShouldRunCursorBlinkTimer(blinkEnabled, focused));
        }

        [Fact]
        public void LosingFocusStopsBlinkingEvenWithTheSettingEnabled()
        {
            // Stated separately from the table because it is the actual regression: before
            // #126 the setting alone decided, so this returned true and cost a render pass.
            Assert.True(TerminalView.ShouldRunCursorBlinkTimer(blinkEnabled: true, focused: true));
            Assert.False(TerminalView.ShouldRunCursorBlinkTimer(blinkEnabled: true, focused: false));
        }
    }
}
