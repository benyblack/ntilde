using Avalonia.Input;
using Ntilde.VT;

namespace Ntilde
{
    /// <summary>
    /// Tracks which siblings already received a text-capable gesture during keydown. This can
    /// be every sibling when the focused pane consumes the key, or just the level-3 receivers.
    /// The next text callback consumes the decision. The matching key release or a new
    /// keydown expires it.
    /// </summary>
    internal sealed class BroadcastTextGate
    {
        private (PhysicalKey Physical, Key Logical)? _armed;

        private string? _expectedText;
        private HashSet<TerminalBuffer?>? _delivered;

        /// <summary>True while a fanned-out gesture's trailing text is still expected.</summary>
        public bool IsArmed => _armed.HasValue;

        /// <summary>Every window keydown starts a new gesture; an older arming no longer applies.</summary>
        public void Clear()
        {
            _armed = null;
            _expectedText = null;
            _delivered = null;
        }

        /// <summary>The key broadcast already delivered this gesture to this sibling.</summary>
        public void MarkFannedOut(Key key, PhysicalKey physical, string? expectedText, TerminalBuffer? receiver)
        {
            _armed = Identity(key, physical);
            _expectedText = expectedText;
            (_delivered ??= []).Add(receiver);
        }

        /// <summary>Return the receivers already served for this text callback, once.</summary>
        public IReadOnlySet<TerminalBuffer?>? ConsumeText(string text)
        {
            var delivered = _expectedText == null || _expectedText == text ? _delivered : null;
            Clear();
            return delivered;
        }

        /// <summary>The matching release ends the gesture, as it does for the view's suppression.</summary>
        public void KeyUp(Key key, PhysicalKey physical)
        {
            if (_armed == Identity(key, physical)) Clear();
        }

        // Same identity rule as TerminalView.KeyIdentity: physical position when known.
        private static (PhysicalKey, Key) Identity(Key key, PhysicalKey physical)
            => physical == PhysicalKey.None ? (physical, key) : (physical, Key.None);
    }
}
