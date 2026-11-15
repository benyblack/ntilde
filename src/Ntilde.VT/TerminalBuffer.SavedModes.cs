namespace Ntilde.VT
{
    public partial class TerminalBuffer
    {
        /// <summary>xterm's ?9 slot: it keeps the mouse tracking selector, as xterm's does, in a slot of its own.</summary>
        internal const int SavedModeX10Slot = 9;

        /// <summary>The slot ?1000, ?1002 and ?1003 share: the mouse tracking selector (xterm's send_mouse_pos).</summary>
        internal const int SavedModeMouseTrackingSlot = 1000;

        /// <summary>The slot ?1005, ?1006 and ?1015 share: the mouse coordinate encoding selector (xterm's extend_coords).</summary>
        internal const int SavedModeMouseEncodingSlot = 1005;

        // The slots, one per saved value. A boolean slot is named by its own mode and holds 0 or 1.
        private static readonly int[] SavedModeSlots = [1, 5, 6, 7, 9, 25, 45, 66, 69, 80, 1000, 1004, 1005, 1007, 2004];

        // One value per slot, -1 while empty. The whole array is swapped on every change, as for the SGR stack.
        private int[] _savedModes = NoSavedModes();

        private static int[] NoSavedModes() => Enumerable.Repeat(-1, SavedModeSlots.Length).ToArray();

        /// <summary>The slot that saves the given DEC private mode, or 0 when XTSAVE and XTRESTORE do nothing for it.</summary>
        internal static int SavedModeSlotFor(int mode) => mode switch
        {
            1002 or 1003 => SavedModeMouseTrackingSlot,
            1006 or 1015 => SavedModeMouseEncodingSlot,
            _ => Array.IndexOf(SavedModeSlots, mode) >= 0 ? mode : 0,
        };

        /// <summary>Keeps one value in one slot, replacing what it held.</summary>
        internal void SetSavedMode(int slot, int value)
        {
            int[] next = (int[])Volatile.Read(ref _savedModes).Clone();
            next[Array.IndexOf(SavedModeSlots, slot)] = value;
            Volatile.Write(ref _savedModes, next);
        }

        /// <summary>The value a slot holds, or false while nothing was saved into it.</summary>
        internal bool TryGetSavedMode(int slot, out int value)
        {
            value = Volatile.Read(ref _savedModes)[Array.IndexOf(SavedModeSlots, slot)];
            return value >= 0;
        }

        private List<TerminalSavedModeEntry>? ExportSavedModes()
        {
            int[] saved = Volatile.Read(ref _savedModes);
            var entries = new List<TerminalSavedModeEntry>();
            for (int i = 0; i < saved.Length; i++)
            {
                if (saved[i] >= 0) entries.Add(new TerminalSavedModeEntry { Mode = SavedModeSlots[i], Value = saved[i] });
            }

            return entries.Count == 0 ? null : entries;
        }

        private void ImportSavedModes(List<TerminalSavedModeEntry>? saved)
        {
            int[] next = NoSavedModes();
            if (saved is not null)
            {
                foreach (TerminalSavedModeEntry entry in saved) next[Array.IndexOf(SavedModeSlots, entry.Mode)] = entry.Value;
            }

            Volatile.Write(ref _savedModes, next);
        }

        private static void ValidateSavedModes(List<TerminalSavedModeEntry>? saved)
        {
            if (saved is null) return;
            if (saved.Count > SavedModeSlots.Length) throw StateTransferValidation.Reject("Saved modes", "more entries than there are slots");
            var seen = new HashSet<int>();
            foreach (TerminalSavedModeEntry candidate in saved)
            {
                TerminalSavedModeEntry entry = StateTransferValidation.RequirePresent(candidate, "Saved mode");
                if (Array.IndexOf(SavedModeSlots, entry.Mode) < 0) throw StateTransferValidation.Reject("Saved mode", "names no slot");
                if (!seen.Add(entry.Mode)) throw StateTransferValidation.Reject("Saved mode", "repeats a slot");
                if (!SavedModeValueAllowed(entry.Mode, entry.Value)) throw StateTransferValidation.Reject("Saved mode", "holds a value its slot cannot");
            }
        }

        private static bool SavedModeValueAllowed(int slot, int value) => slot switch
        {
            SavedModeX10Slot or SavedModeMouseTrackingSlot => value is 0 or 9 or 1000 or 1002 or 1003,
            SavedModeMouseEncodingSlot => value is 0 or 1005 or 1006 or 1015,
            _ => value is 0 or 1,
        };
    }
}
