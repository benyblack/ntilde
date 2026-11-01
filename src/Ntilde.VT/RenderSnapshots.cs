using System;
using System.Collections.Generic;

namespace Ntilde.VT
{
    public readonly struct RenderSnapshotRequest
    {
        public int ViewportRows { get; init; }
        public int ViewportCols { get; init; }
        public int ScrollOffset { get; init; }
        public SelectionState? Selection { get; init; }
        public IReadOnlyList<SearchMatch>? SearchMatches { get; init; }
        public int ActiveSearchIndex { get; init; }

        /// <summary>
        /// True for a capture that is not a frame of the live view: an agent-host screenshot, a
        /// PNG export, a golden render. Such a capture neither reads nor writes the buffer's
        /// row-diff baseline and render-row cache; it builds every row fresh and reports every
        /// renderable row as dirty.
        /// </summary>
        /// <remarks>
        /// The default (false) is the live view's mode. There, <see cref="TerminalRenderSnapshot.DirtySpans"/>
        /// holds the cells that changed since the previous live capture, and the renderer
        /// repaints only those spans over the row picture it drew last time. That contract
        /// breaks if anything else captures in between: the intervening capture advances the
        /// baseline, the next live frame's spans omit every change made before it, and the
        /// renderer keeps painting those cells from its older picture. The baseline belongs to
        /// the live renderer alone, so every other caller must set this.
        /// </remarks>
        public bool Isolated { get; init; }
        /// <summary>An active image-handle session; zero makes this snapshot own its source lease.</summary>
        public int SnapshotSessionId { get; init; }
    }

    /// <summary>A value-only mask: a render consumer cannot mutate the session's palette overrides.</summary>
    public readonly record struct PaletteOverrideMask(ulong First, ulong Second, ulong Third, ulong Fourth)
    {
        public bool Contains(int index)
        {
            if ((uint)index >= 256) return false;
            ulong bits = (index / 64) switch { 0 => First, 1 => Second, 2 => Third, _ => Fourth };
            return (bits & (1UL << (index % 64))) != 0;
        }
    }

    public readonly struct RenderThemeSnapshot
    {
        public TermColor Foreground { get; init; }
        public TermColor Background { get; init; }
        public TermColor CursorColor { get; init; }
        public TermColor[] AnsiPalette { get; init; }
        public bool HasForegroundOverride { get; init; }
        public bool HasBackgroundOverride { get; init; }
        public PaletteOverrideMask PaletteOverrides { get; init; }

        /// <summary>
        /// The theme these colors came from, for the colors the renderer derives from a whole theme
        /// (selection, search, cursor text). Null only in snapshots built by hand.
        /// </summary>
        public TerminalTheme? Source { get; init; }
    }

    public readonly struct DirtySpan
    {
        // End-exclusive range: [ColStart, ColEnd)
        public int Row { get; init; }
        public int ColStart { get; init; }
        public int ColEnd { get; init; }
    }

    public readonly struct SelectionRowSnapshot
    {
        public int AbsRow { get; init; }
        public int ColStart { get; init; }
        public int ColEnd { get; init; }
    }

    public readonly struct SearchHighlightSnapshot
    {
        public int AbsRow { get; init; }
        public int StartCol { get; init; }
        public int EndCol { get; init; }
        public bool IsActive { get; init; }
    }

    public readonly struct PooledArray<T> : IDisposable
    {
        public readonly T[] Array;
        public readonly int Length;

        public PooledArray(T[] array, int length)
        {
            Array = array;
            Length = length;
        }

        public void Dispose()
        {
            if (Array != null)
            {
                System.Buffers.ArrayPool<T>.Shared.Return(Array, clearArray: true);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
            Justification = "Mirrors ImmutableArray<T>.Empty: the empty value belongs on the type it is the empty value of.")]
        public static PooledArray<T> Empty => new();
    }

    public readonly struct TerminalRenderSnapshot : IDisposable
    {
        public int ViewportRows { get; init; }
        public int ViewportCols { get; init; }
        public int TotalLines { get; init; }
        public int AbsDisplayStart { get; init; }
        public int ScrollOffset { get; init; }
        public int CursorRow { get; init; }
        public int CursorCol { get; init; }
        public CursorStyle CursorStyle { get; init; }
        public bool CursorStyleIsDefault { get; init; }
        public RenderThemeSnapshot Theme { get; init; }
        public PooledArray<DirtySpan> DirtySpans { get; init; }
        public PooledArray<RenderRowSnapshot> RowsData { get; init; }
        public PooledArray<RenderImageSnapshot> Images { get; init; }
        internal IDisposable? ImageSourceLease { get; init; }
        public PooledArray<SelectionRowSnapshot> SelectionRows { get; init; }
        public PooledArray<SearchHighlightSnapshot> SearchHighlights { get; init; }

        public void Dispose()
        {
            DirtySpans.Dispose();
            RowsData.Dispose();
            Images.Dispose();
            ImageSourceLease?.Dispose();
            SelectionRows.Dispose();
            SearchHighlights.Dispose();
        }
    }

    public struct RenderCellSnapshot
    {
        public char Character;
        public string? Text;
        public CellTextSizing? TextSizing { get; init; }
        public TermColor Foreground;
        public TermColor Background;
        public bool IsInverse;
        public bool IsBold;
        public bool IsDefaultForeground;
        public bool IsDefaultBackground;
        public bool IsWide;
        public bool IsWideContinuation;
        public bool IsHidden;
        public bool IsFaint;
        public bool IsItalic;
        public bool IsUnderline;
        /// <summary>The underline's style and color when it is more than a straight line in the text color (#270).</summary>
        public CellUnderline? Underline;
        public bool IsBlink;
        public bool IsStrikethrough;
        public short FgIndex;
        public short BgIndex;
    }

    public struct RenderRowSnapshot
    {
        public int AbsRow;
        public uint Revision;
        public int Cols;
        public RenderCellSnapshot[] Cells;
        public long RowId;
    }

    public struct RenderImageSnapshot
    {
        public int CellX;
        public int CellY;
        public int CellWidth;
        public int CellHeight;
        public object ImageHandle;
        internal SixelSource? Source;
        public bool IsSticky;
        public int ClipTopRows { get; init; }
        public int ClipBottomRows { get; init; }
        public int ClipRightColumns { get; init; }
    }
}
