using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;
using Xunit;

namespace Ntilde.Tests.Performance
{
    /// <summary>
    /// Opt-in A/B render timing benchmark (not a gate). Skipped unless
    /// NTILDE_RENDER_BENCH=1. Drives the real TerminalDrawOperation the way
    /// TerminalView does - a fresh op per frame over a persistent row cache,
    /// glyph atlas and fallback chain - into a CPU raster canvas, and times
    /// only construct + DrawTerminalInternal + Dispose (buffer mutation is
    /// untimed). Appends one JSON line per scenario to NTILDE_RENDER_BENCH_OUT,
    /// tagged with NTILDE_RENDER_BENCH_LABEL, so runs of two builds on the same
    /// machine can be compared. Measures CPU rasterisation, not the GPU path.
    /// </summary>
    [Collection("RendererStatistics")]
    public class RenderBenchmark
    {
        private const int Cols = 200;
        private const int Rows = 50;
        private const int WarmupFrames = 60;
        private const int MeasuredFrames = 300;
        private const float FontSize = 14f;

        private readonly ITestOutputHelper _output;

        public RenderBenchmark(ITestOutputHelper output)
        {
            _output = output;
        }

        [AvaloniaFact]
        [Trait("Category", "RenderBench")]
        public void RenderBenchmark_Scenarios()
        {
            Assert.SkipUnless(
                Environment.GetEnvironmentVariable("NTILDE_RENDER_BENCH") == "1",
                "opt-in benchmark; set NTILDE_RENDER_BENCH=1");

            string label = Environment.GetEnvironmentVariable("NTILDE_RENDER_BENCH_LABEL") ?? "unlabelled";
            string? outPath = Environment.GetEnvironmentVariable("NTILDE_RENDER_BENCH_OUT");

            string fontPath = FindBundledFont("JetBrainsMonoNL-Regular.ttf");
            var skTypeface = new SharedSKTypeface(SKTypeface.FromFile(fontPath));
            var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, FontSize)
            {
                Edging = SKFontEdging.Antialias,
                Hinting = SKFontHinting.Normal
            });
            CellMetrics metrics = MeasureCell(skFont.Font!);
            SKTypeface[] fallbackChain = TerminalView.GetSnapshotFallbackChain();

            var scenarios = new (string Name, bool Shaping, Func<int, string> Frame, bool Repaint)[]
            {
                ("dense-color-repaint", false, DenseColorScreen, true),
                ("scroll-append", false, ScrollLine, false),
                ("unicode-repaint", false, UnicodeScreen, true),
                ("shaped-repaint", true, ShapedScreen, true),
            };

            var lines = new List<string>();
            try
            {
            foreach (var s in scenarios)
            {
                var result = RunScenario(s.Name, s.Shaping, s.Frame, s.Repaint, metrics, skTypeface, skFont, fallbackChain);
                string json = JsonSerializer.Serialize(new
                {
                    label,
                    scenario = s.Name,
                    skia = FileVersion(typeof(SKCanvas)),
                    harfbuzz = FileVersion(typeof(HarfBuzzSharp.Buffer)),
                    frames = result.Times.Length,
                    medianMs = Math.Round(Percentile(result.Times, 50), 4),
                    p95Ms = Math.Round(Percentile(result.Times, 95), 4),
                    meanMs = Math.Round(result.Times.Average(), 4),
                    allocKbPerFrame = Math.Round(result.AllocBytes / 1024.0 / result.Times.Length, 2),
                });
                _output.WriteLine(json);
                lines.Add(json);
            }
            }
            finally
            {
                // Shared* wrappers expose Dispose() without IDisposable.
                skFont.Dispose();
                skTypeface.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(outPath))
            {
                File.AppendAllLines(outPath, lines);
            }
        }

        private static (double[] Times, long AllocBytes) RunScenario(
            string name,
            bool shaping,
            Func<int, string> frameText,
            bool repaint,
            CellMetrics metrics,
            SharedSKTypeface skTypeface,
            SharedSKFont skFont,
            SKTypeface[] fallbackChain)
        {
            int width = (int)Math.Ceiling(Cols * metrics.CellWidth);
            int height = (int)Math.Ceiling(Rows * metrics.CellHeight);

            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            using var rowCache = new RowImageCache();
            using var glyphCache = new GlyphCache();
            var fallbackCache = new ConcurrentDictionary<string, SKTypeface?>();
            var selection = new SelectionState();

            var buffer = new TerminalBuffer(Cols, Rows);
            var parser = new AnsiParser(buffer);
            for (int r = 0; r < Rows; r++)
            {
                parser.Process(ScrollLine(r));
            }

            var typeface = new Typeface("JetBrains Mono NL, Consolas, Monospace");
            var glyphTypeface = typeface.GlyphTypeface;

            int total = WarmupFrames + MeasuredFrames;
            var times = new double[MeasuredFrames];
            long allocBytes = 0;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            for (int frame = 0; frame < total; frame++)
            {
                // Untimed: mutate the buffer the way a shell would.
                if (repaint)
                {
                    parser.Process("\x1b[H" + frameText(frame));
                }
                else
                {
                    parser.Process(frameText(frame + Rows));
                }

                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();

                var op = new TerminalDrawOperation(
                    new Rect(0, 0, width, height),
                    buffer,
                    scrollOffset: 0,
                    selection: selection,
                    searchMatches: null,
                    activeSearchIndex: -1,
                    metrics: metrics,
                    typeface: typeface,
                    fontSize: FontSize,
                    glyphTypeface: glyphTypeface,
                    skTypeface: skTypeface,
                    skFont: skFont,
                    enableLigatures: shaping,
                    fallbackCache: fallbackCache,
                    fallbackChain: fallbackChain,
                    opacity: 1.0,
                    hideCursor: true,
                    renderScaling: 1.0,
                    snapshotRows: buffer.Rows,
                    snapshotCols: buffer.Cols,
                    totalLines: buffer.TotalLines,
                    cursorRow: buffer.CursorRow,
                    cursorCol: buffer.CursorCol,
                    rowCache: rowCache,
                    enableComplexShaping: shaping,
                    glyphCache: glyphCache);
                try
                {
                    op.DrawTerminalInternal(canvas);
                }
                finally
                {
                    op.Dispose();
                }
                canvas.Flush();

                long t1 = Stopwatch.GetTimestamp();
                long a1 = GC.GetAllocatedBytesForCurrentThread();

                if (frame >= WarmupFrames)
                {
                    times[frame - WarmupFrames] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    allocBytes += a1 - a0;
                }
            }

            return (times, allocBytes);
        }

        // ls --color / compiler-output style: every row rewritten with SGR runs.
        private static string DenseColorScreen(int frame)
        {
            var sb = new StringBuilder(Rows * (Cols + 64));
            for (int r = 0; r < Rows; r++)
            {
                int col = 0;
                int seg = 0;
                while (col < Cols)
                {
                    int fg = 31 + ((r + seg + frame) % 7);
                    string word = $"w{(frame * 31 + r * 7 + seg) % 9973:D4}";
                    if (col + word.Length + 1 > Cols) break;
                    sb.Append("\x1b[").Append(fg).Append(seg % 3 == 0 ? ";1m" : "m").Append(word).Append("\x1b[0m ");
                    col += word.Length + 1;
                    seg++;
                }
                sb.Append("\x1b[K");
                if (r < Rows - 1) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // One new coloured line per frame; the rest of the screen scrolls.
        private static string ScrollLine(int n)
            => $"\x1b[3{n % 7 + 1}m[{n:D6}]\x1b[0m build: src/Module{n % 97}/File{n % 13}.cs(42,17): warning CS{1000 + n % 900}: " +
               new string((char)('a' + n % 26), 120) + "\r\n";

        // CJK (wide), emoji (fallback, colour), box-drawing and Nerd Font icons.
        private static string UnicodeScreen(int frame)
        {
            string[] pieces = { "日本語の文字", "│├──┤╭─╮", "😀🚀🔥", "", "한국어", "Ωλπ", "✓✗" };
            var sb = new StringBuilder();
            for (int r = 0; r < Rows; r++)
            {
                var line = new StringBuilder();
                int i = r + frame;
                while (line.Length < Cols - 20)
                {
                    line.Append(pieces[i++ % pieces.Length]).Append(' ');
                }
                sb.Append("\x1b[3").Append((r + frame) % 7 + 1).Append('m').Append(line).Append("\x1b[0m\x1b[K");
                if (r < Rows - 1) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // Complex shaping on (HarfBuzz path): operators, combining marks, Arabic.
        private static string ShapedScreen(int frame)
        {
            string[] pieces = { "a => b", "x != y", "i <= n", "p -> q", "été", "مرحبا", "नमस्ते", "fi fl" };
            var sb = new StringBuilder();
            for (int r = 0; r < Rows; r++)
            {
                var line = new StringBuilder();
                int i = r + frame;
                while (line.Length < Cols - 20)
                {
                    line.Append(pieces[i++ % pieces.Length]).Append("  ");
                }
                sb.Append(line).Append("\x1b[K");
                if (r < Rows - 1) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        private static CellMetrics MeasureCell(SKFont font)
        {
            var m = font.Metrics;
            float ascent = -m.Ascent;
            float descent = m.Descent;
            float leading = m.Leading;
            float cellHeight = (float)Math.Ceiling(ascent + descent + leading);
            float gap = cellHeight - (ascent + descent + leading);
            return new CellMetrics
            {
                CellWidth = (float)Math.Ceiling(font.MeasureText("M")),
                CellHeight = cellHeight,
                Baseline = (float)Math.Round(ascent + gap / 2.0f),
                Ascent = ascent,
                Descent = descent,
                Leading = leading
            };
        }

        private static string FindBundledFont(string fileName)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "src", "Ntilde.App", "Assets", "Fonts", fileName);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException($"bundled font {fileName} not found above {AppContext.BaseDirectory}");
        }

        private static string? FileVersion(Type t)
            => FileVersionInfo.GetVersionInfo(t.Assembly.Location).FileVersion;

        private static double Percentile(double[] values, double p)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            double rank = p / 100.0 * (sorted.Length - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
        }
    }
}
