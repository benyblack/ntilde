namespace Ntilde.VT
{
    /// <summary>
    /// xterm's saved DEC private modes: XTSAVE (<c>CSI ? Pm s</c>) and XTRESTORE (<c>CSI ? Pm r</c>).
    /// </summary>
    public partial class AnsiParser
    {
        /// <summary>
        /// Only the exact identity is admitted: the <c>?</c> leader, no intermediate and plain parameters (a colon
        /// subparameter is ignored whole). Each listed mode is handled on its own; a value that names no saved mode, including an
        /// omitted or zero one, does nothing. The slots are the table in <see cref="TerminalBuffer.SavedModeSlotFor"/>.
        /// </summary>
        private void HandleXtermModeSave(in CsiSequence csi)
        {
            if (csi.Leader != '?' || csi.Intermediates.Length != 0 || csi.Separators.Contains(':')) return;
            bool save = csi.Final == 's';
            foreach (int mode in csi.Args)
            {
                if (save) SaveDecPrivateMode(mode);
                else RestoreDecPrivateMode(mode);
            }
        }

        /// <summary>XTSAVE of one mode: keeps its live value in the mode's one slot. ?1048 is xterm's cursor save, the DECSC bank.</summary>
        private void SaveDecPrivateMode(int mode)
        {
            if (mode == 1048)
            {
                SaveCursor();
                return;
            }

            int slot = TerminalBuffer.SavedModeSlotFor(mode);
            if (slot != 0) _buffer.SetSavedMode(slot, LiveSavedModeValue(slot));
        }

        /// <summary>
        /// XTRESTORE of one mode: applies the value its slot holds through the same path as DECSET and DECRST, so a restored
        /// ?6 homes the cursor as DECOM does. A slot nothing was saved into keeps the live value, and a restore does not empty
        /// its slot. ?1048 is xterm's cursor restore.
        /// </summary>
        private void RestoreDecPrivateMode(int mode)
        {
            if (mode == 1048)
            {
                RestoreCursor();
                return;
            }

            int slot = TerminalBuffer.SavedModeSlotFor(mode);
            if (slot == 0 || !_buffer.TryGetSavedMode(slot, out int value)) return;
            switch (slot)
            {
                case TerminalBuffer.SavedModeMouseTrackingSlot:
                case TerminalBuffer.SavedModeX10Slot:
                    _buffer.Modes.SetMouseTrackingSelector(value);
                    break;
                case TerminalBuffer.SavedModeMouseEncodingSlot:
                    _buffer.Modes.SetMouseEncodingSelector(value);
                    break;
                default:
                    HandleDECPrivateMode([mode], value == 1);
                    break;
            }
        }

        private int LiveSavedModeValue(int slot)
        {
            switch (slot)
            {
                case TerminalBuffer.SavedModeMouseTrackingSlot:
                case TerminalBuffer.SavedModeX10Slot:
                    return _buffer.Modes.MouseTrackingSelector;
                case TerminalBuffer.SavedModeMouseEncodingSlot:
                    return _buffer.Modes.MouseEncodingSelector;
                default:
                    return QueryPrivateModeState(slot) == 1 ? 1 : 0;
            }
        }
    }
}
