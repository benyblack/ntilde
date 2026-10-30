using Avalonia;
using Avalonia.Media;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// One terminal view's draw path, frame after frame, off screen (#127): the draw operation built as
/// <see cref="TerminalView"/> builds it, drawn onto a canvas that outlives the frames, with the caches, typefaces and
/// fallback state a view keeps between them. What a frame costs here is what the render thread pays per frame.
/// </summary>
internal sealed class DrawPathHarness : IDisposable
{
    internal static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8.4f,
        CellHeight = 18.0f,
        Baseline = 14.0f,
        Ascent = 14.0f,
        Descent = 4.0f,
    };

    private readonly ConcurrentDictionary<string, SKTypeface?> _fallbackCache = new();
    private readonly SKTypeface[] _fallbackChain = Array.Empty<SKTypeface>();
    private readonly SelectionState _selection = new();
    private readonly Typeface _typeface = new("Cascadia Code, Consolas, Monospace");
    private readonly SharedSKTypeface _skTypeface;
    private readonly SharedSKFont _skFont;
    private readonly SKSurface _surface;
    private readonly Rect _bounds;

    public DrawPathHarness(int cols, int rows, bool withRowCache = true)
    {
        Buffer = new TerminalBuffer(cols, rows);
        Parser = new AnsiParser(Buffer);
        RowCache = withRowCache ? new RowImageCache { MaxEntries = Math.Max(rows * 3, 50) } : null;
        _skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(_typeface.FontFamily.Name));
        _skFont = new SharedSKFont(new SKFont(_skTypeface.Typeface, 14));
        int width = (int)Math.Ceiling((cols * Metrics.CellWidth) + 8);
        int height = (int)Math.Ceiling(rows * Metrics.CellHeight);
        _bounds = new Rect(0, 0, width, height);
        _surface = SKSurface.Create(new SKImageInfo(width, height)) ?? throw new InvalidOperationException("no surface");
    }

    public TerminalBuffer Buffer { get; }

    public AnsiParser Parser { get; }

    public GlyphCache GlyphCache { get; } = new();

    public RowImageCache? RowCache { get; }

    /// <summary>
    /// A screenful of what a shell shows: a prompt, colored listings, bold and underlined words, a 256-color and an RGB
    /// run, and a line of wide characters.
    /// </summary>
    public void FillWithTypicalOutput()
    {
        for (int i = 0; i < Buffer.Rows - 1; i++)
        {
            Parser.Process(TypicalLine(i) + "\r\n");
        }
    }

    public static string TypicalLine(int i) => (i % 4) switch
    {
        0 => $"\x1b[1;32muser@host\x1b[0m:\x1b[1;34m~/src/project\x1b[0m$ ls -la --color src/module_{i}",
        1 => $"-rw-r--r-- 1 user staff  {i * 137,6} Oct 30 12:{i % 60:00} \x1b[38;5;208mbuild_{i}.log\x1b[0m",
        2 => $"\x1b[4mwarning\x1b[0m: unused variable \x1b[1m`value_{i}`\x1b[0m \x1b[38;2;200;120;40mat line {i}\x1b[0m",
        _ => $"\u4F60\u597D\u4E16\u754C {i} - \x1b[7m inverse \x1b[0m \x1b[3mitalic\x1b[0m tail text to fill the row",
    };

    /// <summary>One frame, as the render thread draws it: the operation built, drawn, disposed.</summary>
    public void DrawFrame()
    {
        int rows;
        int cols;
        int totalLines;
        int cursorRow;
        int cursorCol;
        Buffer.Lock.EnterReadLock();
        try
        {
            rows = Buffer.Rows;
            cols = Buffer.Cols;
            totalLines = Buffer.InternalTotalLines;
            cursorRow = Buffer.InternalCursorRow;
            cursorCol = Buffer.InternalCursorCol;
        }
        finally
        {
            Buffer.Lock.ExitReadLock();
        }

        var op = new TerminalDrawOperation(
            _bounds,
            Buffer,
            scrollOffset: 0,
            selection: _selection,
            searchMatches: null,
            activeSearchIndex: -1,
            metrics: Metrics,
            typeface: _typeface,
            fontSize: 14,
            glyphTypeface: _typeface.GlyphTypeface,
            skTypeface: _skTypeface,
            skFont: _skFont,
            enableLigatures: false,
            fallbackCache: _fallbackCache,
            fallbackChain: _fallbackChain,
            opacity: 1.0,
            hideCursor: false,
            renderScaling: 1.0,
            snapshotRows: rows,
            snapshotCols: cols,
            totalLines: totalLines,
            cursorRow: cursorRow,
            cursorCol: cursorCol,
            rowCache: RowCache,
            enableComplexShaping: true,
            glyphCache: GlyphCache);
        try
        {
            op.DrawTerminalInternal(_surface.Canvas);
        }
        finally
        {
            op.Dispose();
        }
    }

    /// <summary>
    /// Bytes the draw path allocates per frame, on average over <paramref name="frames"/> frames: only the frame,
    /// not <paramref name="beforeFrame"/> (the output that changes the screen).
    /// </summary>
    public double AllocatedBytesPerFrame(Action beforeFrame, int frames)
    {
        long bytes = 0;
        for (int i = 0; i < frames; i++)
        {
            beforeFrame();
            long before = GC.GetAllocatedBytesForCurrentThread();
            DrawFrame();
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return bytes / (double)frames;
    }

    /// <summary>Each frame's time in milliseconds, sorted: only the frame, not <paramref name="beforeFrame"/>.</summary>
    public double[] FrameTimesMs(Action beforeFrame, int frames)
    {
        var times = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            beforeFrame();
            long started = Stopwatch.GetTimestamp();
            DrawFrame();
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(times);
        return times;
    }

    /// <summary>The <paramref name="percentile"/> (0-100) of sorted frame times.</summary>
    public static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];

    public void Dispose()
    {
        _surface.Dispose();
        GlyphCache.Dispose();
        RowCache?.Dispose();
        _skFont.Dispose();
        _skTypeface.Dispose();
    }
}
