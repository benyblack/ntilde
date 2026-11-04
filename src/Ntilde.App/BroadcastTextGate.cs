using Avalonia.Input;

namespace Ntilde
{
    /// <summary>
    /// One-shot coordination between tab broadcast's keydown and text paths (#829). When the
    /// focused pane consumes a text-capable chord itself (Shift+Space at modifyOtherKeys level 2),
    /// the key broadcast fans the gesture out to the siblings during keydown. Some backends still
    /// raise TextInput after a handled keydown - the case the focused view's own one-shot
    /// suppression exists for - and the window's text broadcast tunnel would then send it again.
    /// This gate mirrors that view-side lifetime: armed by the fan-out, consumed by the first
    /// following text callback, and expired by the gesture's key release or the next keydown, so
    /// a later unrelated text commit is never swallowed.
    /// </summary>
    internal sealed class BroadcastTextGate
    {
        private (PhysicalKey Physical, Key Logical)? _armed;

        /// <summary>True while a fanned-out gesture's trailing text is still expected.</summary>
        public bool IsArmed => _armed.HasValue;

        /// <summary>Every window keydown starts a new gesture; an older arming no longer applies.</summary>
        public void KeyDown() => _armed = null;

        /// <summary>The key broadcast already delivered this gesture to every sibling.</summary>
        public void MarkFannedOut(Key key, PhysicalKey physical) => _armed = Identity(key, physical);

        /// <summary>True, once, when a text callback belongs to a gesture already broadcast.</summary>
        public bool TryConsumeText()
        {
            if (!_armed.HasValue) return false;
            _armed = null;
            return true;
        }

        /// <summary>The matching release ends the gesture, as it does for the view's suppression.</summary>
        public void KeyUp(Key key, PhysicalKey physical)
        {
            if (_armed == Identity(key, physical)) _armed = null;
        }

        // Same identity rule as TerminalView.KeyIdentity: physical position when known.
        private static (PhysicalKey, Key) Identity(Key key, PhysicalKey physical)
            => physical == PhysicalKey.None ? (physical, key) : (physical, Key.None);
    }
}
