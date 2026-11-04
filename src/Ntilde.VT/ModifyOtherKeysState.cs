using System.Globalization;

namespace Ntilde.VT
{
    /// <summary>
    /// xterm's <c>modifyOtherKeys</c> key-modifier resource (XTMODKEYS resource 4), negotiated
    /// with <c>CSI &gt; 4 ; Pv m</c>, reset with <c>CSI &gt; 4 m</c> / <c>CSI &gt; 4 ; m</c> /
    /// <c>CSI &gt; m</c>, disabled with <c>CSI &gt; 4 n</c> and queried with XTQMODKEYS
    /// <c>CSI ? 4 m</c> (reply <c>CSI &gt; 4 ; Pv m</c>).
    ///
    /// Only levels 0 (off), 1 and 2 are honored. Level 3 (xterm's "modify all ordinary keys,
    /// even unmodified") and any other value is ignored and leaves the current level in place,
    /// so the query never advertises a level whose encoding is not implemented - the same
    /// "report only what is in effect" rule <see cref="KittyKeyboardState"/> follows.
    ///
    /// DEVIATION from xterm: xterm keeps one session-wide value; Ntilde keeps one level per
    /// screen buffer, like the kitty keyboard stacks, so an editor that raises the level inside
    /// the alternate screen cannot leave the shell's main screen in that mode. Vim, tmux and
    /// Emacs all switch to the alternate screen before they send <c>CSI &gt; 4 ; Pv m</c> and
    /// reset it before they leave, so the observable behavior for them is unchanged.
    /// </summary>
    public sealed class ModifyOtherKeysState
    {
        /// <summary>The highest level whose encoding the App layer implements.</summary>
        public const int MaxSupportedLevel = 2;

        private readonly object _gate = new();
        private int _mainLevel;
        private int _altLevel;
        private bool _altActive;

        // Cached active level, read lock-free by the UI thread on every keystroke while the PTY
        // reader thread mutates the per-screen values (mirrors KittyKeyboardState._currentFlags).
        private volatile int _currentLevel;
        private volatile bool _isSupported = true;

        /// <summary>
        /// The level in effect for the active screen buffer: 0, 1 or 2, or 0 while the input host
        /// cannot encode it (<see cref="IsSupported"/>). The App encoder and the query read this.
        /// </summary>
        public int Level => _isSupported ? _currentLevel : 0;

        /// <summary>The level the child requested for the active screen, regardless of the ceiling.</summary>
        public int RequestedLevel => _currentLevel;

        /// <summary>
        /// The input host's capability ceiling, like <see cref="KittyKeyboardState.SupportedFlagMask"/>:
        /// while false the effective <see cref="Level"/> is 0, so neither the query nor the encoder
        /// claims modifyOtherKeys, but requested levels keep being tracked and return when it is
        /// restored. A shared session's daemon clears it while an interactive client that cannot
        /// encode it (the text client, an older GUI) is attached, and sends it to its GUI clients.
        /// </summary>
        public bool IsSupported => _isSupported;

        /// <summary>Sets the capability ceiling; see <see cref="IsSupported"/>.</summary>
        public void SetSupported(bool supported) => _isSupported = supported;

        /// <summary>The main screen's level (state transfer, diagnostics and tests).</summary>
        public int MainLevel
        {
            get { lock (_gate) { return _mainLevel; } }
        }

        /// <summary>The alternate screen's level (state transfer, diagnostics and tests).</summary>
        public int AltLevel
        {
            get { lock (_gate) { return _altLevel; } }
        }

        /// <summary>True when the alternate screen's level is the active one.</summary>
        public bool IsAltScreenActive
        {
            get { lock (_gate) { return _altActive; } }
        }

        /// <summary>True for a level this terminal honors.</summary>
        public static bool IsSupportedLevel(int level) => level >= 0 && level <= MaxSupportedLevel;

        /// <summary>
        /// Sets the active screen's level. Returns false, changing nothing, for an unsupported
        /// level (see the type remarks).
        /// </summary>
        public bool SetLevel(int level)
        {
            if (!IsSupportedLevel(level)) return false;
            lock (_gate)
            {
                if (_altActive) _altLevel = level;
                else _mainLevel = level;
                _currentLevel = level;
            }

            return true;
        }

        /// <summary>
        /// Selects which per-screen-buffer level is active. Called by the buffer whenever it
        /// enters or leaves the alternate screen.
        /// </summary>
        public void SetActiveScreen(bool isAltScreen)
        {
            lock (_gate)
            {
                _altActive = isAltScreen;
                _currentLevel = isAltScreen ? _altLevel : _mainLevel;
            }
        }

        /// <summary>
        /// Returns both screens to level 0 without changing which screen is active. xterm's
        /// RIS and DECSTR both restore the resource's initial value (0), and the shell-prompt
        /// transient reset uses it for the same reason it clears the kitty stacks.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _mainLevel = 0;
                _altLevel = 0;
                _currentLevel = 0;
            }
        }

        /// <summary>XTQMODKEYS reply for resource 4: <c>CSI &gt; 4 ; Pv m</c>, with the effective level.</summary>
        public string FormatQueryResponse()
            => string.Concat("\x1b[>4;", Level.ToString(CultureInfo.InvariantCulture), "m");

        public ModifyOtherKeysState Clone()
        {
            var clone = new ModifyOtherKeysState();
            lock (_gate)
            {
                clone._mainLevel = _mainLevel;
                clone._altLevel = _altLevel;
                clone._altActive = _altActive;
                clone._currentLevel = _currentLevel;
                clone._isSupported = _isSupported;
            }

            return clone;
        }

        /// <summary>
        /// Replaces both levels and the active-screen selection from a validated
        /// <see cref="TerminalStateSnapshot"/>. Unsupported values are already rejected by
        /// validation; they are clamped here as well so no caller can install one.
        /// </summary>
        internal void ImportForState(int mainLevel, int altLevel, bool altActive)
        {
            lock (_gate)
            {
                _mainLevel = IsSupportedLevel(mainLevel) ? mainLevel : 0;
                _altLevel = IsSupportedLevel(altLevel) ? altLevel : 0;
                _altActive = altActive;
                _currentLevel = altActive ? _altLevel : _mainLevel;
            }
        }
    }
}
