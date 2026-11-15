namespace Ntilde.VT
{
    public partial class TerminalBuffer
    {
        /// <summary>xterm's stack depth: "The stack is limited to 10 levels" (ctlseqs, XTPUSHSGR).</summary>
        internal const int MaxSgrStackDepth = 10;

        // XTPUSHSGR parameter bits, one per documented Ps value (ctlseqs: 1 bold, 2 faint, 3 italic, 4 underlined, 5 blink,
        // 7 inverse, 8 invisible, 9 crossed-out, 21 doubly-underlined, 30 foreground, 31 background).
        internal const int SgrStackBold = 1 << 0;
        internal const int SgrStackFaint = 1 << 1;
        internal const int SgrStackItalic = 1 << 2;
        internal const int SgrStackUnderline = 1 << 3;
        internal const int SgrStackBlink = 1 << 4;
        internal const int SgrStackInverse = 1 << 5;
        internal const int SgrStackInvisible = 1 << 6;
        internal const int SgrStackCrossedOut = 1 << 7;
        internal const int SgrStackDoubleUnderline = 1 << 8;
        internal const int SgrStackForeground = 1 << 9;
        internal const int SgrStackBackground = 1 << 10;

        /// <summary>Every attribute the stack can hold: what a push with no parameter saves.</summary>
        internal const int SgrStackAll = (1 << 11) - 1;

        // Published frames are immutable: every change swaps the whole array, so the exporter never sees a half-written stack.
        private TerminalSgrStackEntry[] _sgrStack = [];

        /// <summary>The bit one XTPUSHSGR parameter selects, or 0 for a value ctlseqs does not list.</summary>
        internal static int SgrStackBitFor(int parameter) => parameter switch
        {
            1 => SgrStackBold,
            2 => SgrStackFaint,
            3 => SgrStackItalic,
            4 => SgrStackUnderline,
            5 => SgrStackBlink,
            7 => SgrStackInverse,
            8 => SgrStackInvisible,
            9 => SgrStackCrossedOut,
            21 => SgrStackDoubleUnderline,
            30 => SgrStackForeground,
            31 => SgrStackBackground,
            _ => 0,
        };

        /// <summary>The number of saved levels (0..10).</summary>
        internal int SgrStackDepth => Volatile.Read(ref _sgrStack).Length;

        /// <summary>
        /// XTPUSHSGR: saves the selected video attributes of the live pen as one more level. A push onto a full stack is
        /// ignored, so the ten oldest levels stay and an unbalanced program cannot lose the outermost one.
        /// </summary>
        internal void PushSgr(int mask)
        {
            TerminalSgrStackEntry[] stack = Volatile.Read(ref _sgrStack);
            if (stack.Length >= MaxSgrStackDepth) return;
            var next = new TerminalSgrStackEntry[stack.Length + 1];
            Array.Copy(stack, next, stack.Length);
            next[^1] = new TerminalSgrStackEntry { Mask = mask & SgrStackAll, Pen = ExportLiveSgrNoLock() };
            Volatile.Write(ref _sgrStack, next);
        }

        /// <summary>
        /// XTPOPSGR: restores the attributes the top level saved, to the values it saved, and removes the level. Attributes
        /// the level did not select, the overline, the underline color and character protection keep their current value. A pop
        /// with nothing saved is ignored.
        /// </summary>
        internal void PopSgr()
        {
            TerminalSgrStackEntry[] stack = Volatile.Read(ref _sgrStack);
            if (stack.Length == 0) return;
            TerminalSgrStackEntry top = stack[^1];
            Volatile.Write(ref _sgrStack, stack[..^1]);
            RestoreSgrAttributes(top.Mask, top.Pen);
        }

        private void RestoreSgrAttributes(int mask, TerminalSgrState pen)
        {
            if ((mask & SgrStackBold) != 0) IsBold = pen.IsBold;
            if ((mask & SgrStackFaint) != 0) IsFaint = pen.IsFaint;
            if ((mask & SgrStackItalic) != 0) IsItalic = pen.IsItalic;
            if ((mask & SgrStackBlink) != 0) IsBlink = pen.IsBlink;
            if ((mask & SgrStackInverse) != 0) IsInverse = pen.IsInverse;
            if ((mask & SgrStackInvisible) != 0) IsHidden = pen.IsHidden;
            if ((mask & SgrStackCrossedOut) != 0) IsStrikethrough = pen.IsStrikethrough;
            // One underline pen stands for both documented values (xterm keeps a flag each for the single and the double line).
            if ((mask & (SgrStackUnderline | SgrStackDoubleUnderline)) != 0)
            {
                IsUnderline = pen.IsUnderline;
                UnderlineStyle = (UnderlineStyle)pen.UnderlineStyle;
            }

            if ((mask & SgrStackForeground) != 0)
            {
                if (pen.IsDefaultForeground)
                {
                    SyncDefaultForegroundToTheme();
                    CurrentFgIndex = -1;
                }
                else
                {
                    CurrentForeground = TermColor.FromUint(pen.Foreground);
                    CurrentFgIndex = pen.FgIndex;
                    IsDefaultForeground = false;
                }
            }

            if ((mask & SgrStackBackground) != 0)
            {
                if (pen.IsDefaultBackground)
                {
                    SyncDefaultBackgroundToTheme();
                    CurrentBgIndex = -1;
                }
                else
                {
                    CurrentBackground = TermColor.FromUint(pen.Background);
                    CurrentBgIndex = pen.BgIndex;
                    IsDefaultBackground = false;
                }
            }
        }

        private List<TerminalSgrStackEntry>? ExportSgrStack()
        {
            TerminalSgrStackEntry[] stack = Volatile.Read(ref _sgrStack);
            if (stack.Length == 0) return null;
            return stack.Select(entry => new TerminalSgrStackEntry { Mask = entry.Mask, Pen = ClonePen(entry.Pen) }).ToList();
        }

        private void ImportSgrStack(List<TerminalSgrStackEntry>? saved)
            => Volatile.Write(ref _sgrStack, saved is null
                ? []
                : saved.Select(entry => new TerminalSgrStackEntry { Mask = entry.Mask, Pen = ClonePen(entry.Pen) }).ToArray());

        private static TerminalSgrState ClonePen(TerminalSgrState pen) => new()
        {
            Foreground = pen.Foreground,
            Background = pen.Background,
            FgIndex = pen.FgIndex,
            BgIndex = pen.BgIndex,
            IsDefaultForeground = pen.IsDefaultForeground,
            IsDefaultBackground = pen.IsDefaultBackground,
            IsInverse = pen.IsInverse,
            IsBold = pen.IsBold,
            IsFaint = pen.IsFaint,
            IsItalic = pen.IsItalic,
            IsUnderline = pen.IsUnderline,
            IsOverline = pen.IsOverline,
            IsCharacterProtected = pen.IsCharacterProtected,
            UnderlineStyle = pen.UnderlineStyle,
            UnderlineColor = pen.UnderlineColor,
            UnderlineColorIndex = pen.UnderlineColorIndex,
            IsBlink = pen.IsBlink,
            IsStrikethrough = pen.IsStrikethrough,
            IsHidden = pen.IsHidden,
        };

        private static void ValidateSgrStack(List<TerminalSgrStackEntry>? saved)
        {
            if (saved is null) return;
            if (saved.Count > MaxSgrStackDepth) throw StateTransferValidation.Reject("SGR stack", "more levels than the stack holds");
            foreach (TerminalSgrStackEntry entry in saved)
            {
                TerminalSgrStackEntry present = StateTransferValidation.RequirePresent(entry, "SGR stack level");
                StateTransferValidation.RequireInRange(present.Mask, 0, SgrStackAll, "SGR stack level attributes");
                TerminalSgrState pen = StateTransferValidation.RequirePresent(present.Pen, "SGR stack level pen");
                ValidateUnderlinePen(pen, "SGR stack level");
                StateTransferValidation.RequireInRange(pen.FgIndex, -1, 255, "SGR stack level foreground palette color");
                StateTransferValidation.RequireInRange(pen.BgIndex, -1, 255, "SGR stack level background palette color");
            }
        }
    }
}
