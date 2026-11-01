using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.VT;

namespace Ntilde.Replay
{
    internal sealed record RecorderSinkOptions(int MaxPendingBytes, Func<Stream> OpenStream, int? FlushTimeoutMs = null);

    public class PtyRecorder : IDisposable
    {
        private readonly string _filePath;
        public Guid Id { get; } = Guid.NewGuid();
        public const int DefaultMaxPendingBytes = 16 * 1024 * 1024;
        // Includes the record currently being written. Producers never wait for disk I/O.
        private readonly BlockingCollection<byte[]> _queue = new();
        private readonly object _producerGate = new();
        private const int RecordOverheadBytes = 32;
        private readonly int _maxPendingBytes;
        private readonly int? _flushTimeoutMs;
        private readonly object _cancellationGate = new();
        private Task _cancellation = Task.CompletedTask;
        private bool _writerFinished;
        private long _pendingBytes;
        private bool _accepting = true;
        private RecordingFailure? _failure;
        private readonly Func<Stream> _openStream;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<RecordingFailure>? Failed;
        public RecordingFailure? Failure => Volatile.Read(ref _failure);
        public long PendingBytes => Interlocked.Read(ref _pendingBytes);
        public bool IsAccepting => Volatile.Read(ref _accepting) && Failure is null;
        public bool IsReady => _ready.Task.IsCompletedSuccessfully && Failure is null;
        public Task Completion => _writeTask;
        private readonly Task _writeTask;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly DateTime _startTime;
        private readonly ReplayHeader _header;
        private RecordingCellMetrics? _sixelCellSize;

        public PtyRecorder(string filePath, int cols, int rows, string shell = "", RecordingCellMetrics? sixelCellSize = null)
            : this(filePath, cols, rows, shell, sixelCellSize, new RecorderSinkOptions(DefaultMaxPendingBytes,
                () => new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true)))
        { }

        internal PtyRecorder(string filePath, int cols, int rows, string shell, RecordingCellMetrics? sixelCellSize,
            RecorderSinkOptions sink)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sink.MaxPendingBytes);
            _maxPendingBytes = sink.MaxPendingBytes;
            _flushTimeoutMs = sink.FlushTimeoutMs;
            _openStream = sink.OpenStream;
            _filePath = filePath;
            _startTime = DateTime.UtcNow;

            _sixelCellSize = RecordingCellMetrics.FromOptional(sixelCellSize?.WidthPx, sixelCellSize?.HeightPx);
            _header = new ReplayHeader
            {
                Type = ReplayHeader.TypeToken,
                Cols = cols,
                Rows = rows,
                SixelCellWidthPx = sixelCellSize?.WidthPx,
                SixelCellHeightPx = sixelCellSize?.HeightPx,
                Shell = shell
            };

            _writeTask = Task.Run(WriteLoop);
        }

        /// <summary>Writes original bytes and their metric context at the parser host's ordering boundary.</summary>
        public static void RecordOutputAtBoundary(PtyRecorder? recorder, FlightRecordingBuffer? flight,
            RecordingCellMetrics size, ReadOnlyMemory<byte> output)
        {
            if (output.IsEmpty || ((recorder is null || !recorder.IsAccepting) && flight is null)) return;
            byte[] data = MemoryMarshal.TryGetArray(output, out var segment) && segment.Offset == 0 && segment.Array is { } array
                ? array : output.ToArray();
            recorder?.RecordSixelCellSize(size);
            recorder?.RecordChunk(data, output.Length);
            flight?.RecordSixelCellSize(size);
            flight?.RecordChunk(data, output.Length);
        }

        public void RecordChunk(byte[] data, int length)
        {
            RecordChunkAt(GetTimestamp(), data, length);
        }

        /// <summary>
        /// Records an output chunk with an explicit time offset instead of the
        /// enqueue-time clock. Used when re-emitting previously captured events
        /// (e.g. flight-recorder export) so the file preserves original inter-chunk
        /// timing. Callers are responsible for supplying non-decreasing offsets.
        /// </summary>
        public void RecordChunkAt(long timeOffsetMs, byte[] data, int length)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (timeOffsetMs < 0) throw new ArgumentOutOfRangeException(nameof(timeOffsetMs), timeOffsetMs, "Time offset must be non-negative.");
            if (length <= 0 || length > data.Length) return;
            if (!IsAccepting) return;

            lock (_producerGate)
            {
                if (!IsAccepting) return;
                if (((long)length + 2) / 3 * 4 > _maxPendingBytes - PendingBytes)
                {
                    Fail(new RecordingFailure(RecordingFailureKind.Overflow, "Recording stopped: pending output exceeded the byte budget."));
                    return;
                }
                TryEnqueue(new ReplayEvent
                {
                    TimeOffsetMs = timeOffsetMs,
                    Type = "data",
                    Data = Convert.ToBase64String(data, 0, length)
                });
            }
        }

        public void RecordResize(int cols, int rows)
        {
            RecordResizeAt(GetTimestamp(), cols, rows);
        }

        /// <summary>
        /// Records a resize with an explicit time offset. See <see cref="RecordChunkAt"/>.
        /// </summary>
        public void RecordResizeAt(long timeOffsetMs, int cols, int rows)
        {
            if (timeOffsetMs < 0) throw new ArgumentOutOfRangeException(nameof(timeOffsetMs), timeOffsetMs, "Time offset must be non-negative.");
            if (cols <= 0 || rows <= 0) return;
            if (!IsAccepting) return;

            TryEnqueue(new ReplayEvent
            {
                TimeOffsetMs = timeOffsetMs,
                Type = "resize",
                Cols = cols,
                Rows = rows
            });
        }

        /// <summary>
        /// Kept for source compatibility. New recordings never persist typed input, including
        /// passwords entered without echo. Legacy input events remain readable by ReplayRunner.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The instance method is retained for source compatibility; recordings no longer persist input.")]
        public void RecordInput(string input)
        {
            // Intentionally ignored: retaining this API must not persist typed secrets.
        }

        public void RecordSixelCellSize(float width, float height) => RecordSixelCellSize(new RecordingCellMetrics(width, height));

        public void RecordSixelCellSize(RecordingCellMetrics size)
        {
            if (Volatile.Read(ref _sixelCellSize) == size) return;
            RecordSixelCellSizeAt(GetTimestamp(), size.WidthPx, size.HeightPx);
        }

        public void RecordSixelCellSizeAt(long timeOffsetMs, float width, float height)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(timeOffsetMs);
            var cellSize = RecordingCellMetrics.FromOptional(width, height);
            Volatile.Write(ref _sixelCellSize, cellSize);
            if (!IsAccepting) return;
            TryEnqueue(new ReplayEvent { Type = "sixelCellSize", TimeOffsetMs = timeOffsetMs, SixelCellWidthPx = width, SixelCellHeightPx = height });
        }

        /// <summary>
        /// Captures viewport fields and, when a parser is supplied, a complete terminal/parser cut.
        /// Buffer-only snapshots preserve the replay parser's existing continuation; callers
        /// requiring a complete seek checkpoint must supply the parser.
        /// </summary>
        public void RecordSnapshot(TerminalBuffer buffer, AnsiParser? parser = null)
        {
            if (!IsAccepting) return;
            // Buffer before producer gate: output may enqueue while owning the buffer write lock.
            buffer.Lock.EnterReadLock();
            try
            {
                lock (_producerGate)
                {
                    if (!IsAccepting) return;
                    var cellSize = SnapshotCellMetrics(parser);
                    ReplaySnapshot snapshot;
                    var rows = buffer.ViewportRows;
                    int cols = buffer.Cols;
                    int rowCount = buffer.Rows;
                    if ((long)cols * rowCount * Unsafe.SizeOf<TerminalCell>() * 2 > _maxPendingBytes - PendingBytes)
                    {
                        Fail(new RecordingFailure(RecordingFailureKind.Overflow, "Recording stopped: checkpoint exceeded the byte budget."));
                        return;
                    }


                    // A complete cut needs the actual parser. Legacy buffer-only callers must
                    // not replace an in-progress replay parser with fabricated initial state.
                    TerminalStateSnapshot? state = null;
                    if (parser is not null)
                    {
                        state = buffer.ExportState(maxScrollbackRows: 0);
                        state.Parser = parser.ExportState();
                    }
                    snapshot = new ReplaySnapshot
                    {
                        State = state,
                        Cols = cols,
                        Rows = rowCount,
                        CursorCol = buffer.InternalCursorCol,
                        CursorRow = buffer.InternalCursorRow,
                        IsAltScreen = buffer.IsAltScreenActive,
                        ScrollTop = buffer.ScrollTop,
                        ScrollBottom = buffer.ScrollBottom,

                        IsAutoWrapMode = buffer.Modes.IsAutoWrapMode,
                        IsApplicationCursorKeys = buffer.Modes.IsApplicationCursorKeys,
                        IsOriginMode = buffer.Modes.IsOriginMode,
                        IsSixelDisplayMode = buffer.Modes.IsSixelDisplayMode,
                        SixelCellWidthPx = cellSize?.WidthPx,
                        SixelCellHeightPx = cellSize?.HeightPx,
                        IsBracketedPasteMode = buffer.Modes.IsBracketedPasteMode,
                        IsCursorVisible = buffer.Modes.IsCursorVisible,
                        IsPendingWrap = buffer.IsPendingWrap,

                        CurrentForeground = buffer.CurrentForeground.ToUint(),
                        CurrentBackground = buffer.CurrentBackground.ToUint(),
                        CurrentFgIndex = buffer.CurrentFgIndex,
                        CurrentBgIndex = buffer.CurrentBgIndex,
                        IsDefaultForeground = buffer.IsDefaultForeground,
                        IsDefaultBackground = buffer.IsDefaultBackground,
                        IsInverse = buffer.IsInverse,
                        IsBold = buffer.IsBold,
                        IsFaint = buffer.IsFaint,
                        IsItalic = buffer.IsItalic,
                        IsUnderline = buffer.IsUnderline,
                        UnderlineStyle = (byte)buffer.UnderlineStyle,
                        UnderlineColor = buffer.UnderlineColor?.ToUint(),
                        UnderlineColorIndex = buffer.UnderlineColorIndex,
                        IsBlink = buffer.IsBlink,
                        IsStrikethrough = buffer.IsStrikethrough,
                        IsHidden = buffer.IsHidden,
                        ExtendedText = new Dictionary<int, string>(),
                        RowWraps = new bool[rowCount]
                    };

                    // Allocate a single array for all cells in viewport
                    TerminalCell[] allCells = new TerminalCell[cols * rowCount];

                    for (int r = 0; r < rowCount; r++)
                    {
                        var row = rows[r];
                        snapshot.RowWraps[r] = row.IsWrapped;

                        // Copy cells
                        Array.Copy(row.Cells, 0, allCells, r * cols, cols);

                        // Capture extended text
                        for (int c = 0; c < cols; c++)
                        {
                            if (row.Cells[c].HasExtendedText && row.GetExtendedText(c) is { } text)
                                snapshot.ExtendedText[r * cols + c] = text;

                            // Underline style and color (#270)
                            if (row.Cells[c].IsUnderline && row.GetUnderline(c) is { } underline)
                            {
                                (snapshot.Underlines ??= new Dictionary<int, TerminalUnderlineEntry>())[r * cols + c] = new TerminalUnderlineEntry
                                {
                                    Style = (byte)underline.Style,
                                    Color = underline.Color?.ToUint(),
                                    ColorIndex = underline.ColorIndex,
                                };
                            }
                        }
                    }

                    // Convert allCells to Base64 using zero-copy casting
                    var byteSpan = MemoryMarshal.AsBytes(allCells.AsSpan());
                    snapshot.CellsBase64 = Convert.ToBase64String(byteSpan);
                    snapshot.CellsSizeOf = Unsafe.SizeOf<TerminalCell>();
                    snapshot.CellsLayoutId = TerminalCell.TerminalCellLayoutId;

                    TryEnqueue(new ReplayEvent
                    {
                        TimeOffsetMs = GetTimestamp(),
                        Type = "snapshot",
                        Snapshot = snapshot
                    });
                }
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        private RecordingCellMetrics? SnapshotCellMetrics(AnsiParser? parser)
        {
            if (parser is null) return Volatile.Read(ref _sixelCellSize);
            SixelCellSize? size = parser.SixelCellSizeOverride;
            return new RecordingCellMetrics(size?.WidthPx ?? parser.CellWidth, size?.HeightPx ?? parser.CellHeight);
        }

        public void RecordMarker(string name)
        {
            if (!IsAccepting) return;

            TryEnqueue(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "marker",
                MarkerName = name
            });
        }

        private long GetTimestamp() => (long)(DateTime.UtcNow - _startTime).TotalMilliseconds;

        private void TryEnqueue(ReplayEvent ev)
        {
            lock (_producerGate)
            {
                if (!IsAccepting) return;
                try
                {
                    using var bytes = new BoundedRecordBuffer(_maxPendingBytes);
                    using (var json = new Utf8JsonWriter(bytes))
                    {
                        JsonSerializer.Serialize(json, ev, ReplayJsonContext.Default.ReplayEvent);
                    }
                    byte[] record = bytes.FinishLine((int)Math.Max(0, _maxPendingBytes - PendingBytes - RecordOverheadBytes));
                    Interlocked.Add(ref _pendingBytes, record.Length + RecordOverheadBytes);
                    _queue.Add(record, CancellationToken.None);
                }
                catch (RecordingException ex) { Fail(ex.Failure); }
                catch (Exception ex) { Fail(new RecordingFailure(RecordingFailureKind.Write, ex.Message)); }
            }
        }

        private void Fail(RecordingFailure failure)
        {
            failure = failure with { RecordingId = Id };
            if (Interlocked.CompareExchange(ref _failure, failure, null) is not null) return;
            lock (_producerGate)
            {
                _accepting = false;
                _queue.CompleteAdding();
                while (_queue.TryTake(out byte[]? record)) Interlocked.Add(ref _pendingBytes, -record.Length - RecordOverheadBytes);
            }
            lock (_cancellationGate)
            {
                if (!_writerFinished) _cancellation = _cts.CancelAsync();
            }
            _ready.TrySetResult();
            TerminalLogger.Warning($"[PtyRecorder] {_filePath}: {failure.Message}");
            // Observers may stop a session. Never invoke them on the writer or under the producer gate.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Failed?.Invoke(failure); }
                catch (Exception ex) { TerminalLogger.Warning($"[PtyRecorder] Failure observer: {ex.Message}"); }
            });
        }

        public async Task WaitUntilReadyAsync()
        {
            await _ready.Task.ConfigureAwait(false);
            ThrowIfFailed();
        }

        public void ThrowIfFailed()
        {
            if (Failure is { } failure) throw new RecordingException(failure);
        }

        public async Task CompleteAsync()
        {
            lock (_producerGate)
            {
                _accepting = false;
                _queue.CompleteAdding();
            }
            if (Failure is { Kind: RecordingFailureKind.FlushTimeout }) ThrowIfFailed();
            try { await _writeTask.WaitAsync(TimeSpan.FromMilliseconds(_flushTimeoutMs ?? ReadEnvInt("NTILDE_RECORDER_FLUSH_MS", 10000)), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Fail(new RecordingFailure(RecordingFailureKind.FlushTimeout, "Recording did not finish saving before the flush deadline."));
            }
            ThrowIfFailed();
        }

        // Utf8JsonWriter may reserve more than it commits. Scratch is bounded separately;
        // Advance and FinishLine enforce the exact serialized and pending-byte limits.
        private sealed class BoundedRecordBuffer(int limit) : IBufferWriter<byte>, IDisposable
        {
            private byte[] _buffer = Array.Empty<byte>();
            private int _written;
            private static RecordingException Overflow() => new(new RecordingFailure(
                RecordingFailureKind.Overflow, "Recording stopped: pending events exceeded the byte budget."));
            private void EnsureCapacity(int count)
            {
                // Reservations include the encoder's worst-case escaping expansion, even for plain ASCII.
                long largestReservation = Math.Min(int.MaxValue, 6L * limit + 256);
                long maxScratch = Math.Min(int.MaxValue, (long)limit + largestReservation);
                if (count > largestReservation || (long)_written + count > maxScratch) throw Overflow();
                if (_buffer.Length - _written >= count) return;
                byte[] previous = _buffer;
                int capacity = (int)Math.Min(maxScratch, Math.Max((long)_written + count, Math.Max(4096L, (long)_buffer.Length * 2)));
                _buffer = ArrayPool<byte>.Shared.Rent(capacity);
                previous.AsSpan(0, _written).CopyTo(_buffer);
                if (previous.Length != 0) ArrayPool<byte>.Shared.Return(previous, clearArray: true);
            }
            public void Advance(int count)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                if (count > limit - _written) throw Overflow();
                _written += count;
            }
            public Memory<byte> GetMemory(int sizeHint = 0) { EnsureCapacity(Math.Max(1, sizeHint)); return _buffer.AsMemory(_written); }
            public Span<byte> GetSpan(int sizeHint = 0) { EnsureCapacity(Math.Max(1, sizeHint)); return _buffer.AsSpan(_written); }
            public void Dispose()
            {
                if (_buffer.Length == 0) return;
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                _buffer = Array.Empty<byte>();
            }
            public byte[] FinishLine(int remaining)
            {
                if (_written + 1 > remaining) throw Overflow();
                EnsureCapacity(1);
                _buffer[_written++] = (byte)'\n';
                return _buffer.AsSpan(0, _written).ToArray();
            }
        }

        private static int ReadEnvInt(string name, int defaultValue)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (!int.TryParse(raw, out int parsed) || parsed <= 0)
            {
                return defaultValue;
            }

            return parsed;
        }

        private async Task WriteLoop()
        {
            bool opened = false;
            try
            {
                await using Stream stream = _openStream();
                byte[] header = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_header, ReplayJsonContext.Default.ReplayHeader) + "\n");
                await stream.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                opened = true;
                _ready.TrySetResult();
                foreach (byte[] record in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        if (Failure is not null) break;
                        await stream.WriteAsync(record, _cts.Token).ConfigureAwait(false);
                    }
                    finally { Interlocked.Add(ref _pendingBytes, -record.Length - RecordOverheadBytes); }
                }
                if (Failure is null) await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail(new RecordingFailure(opened ? RecordingFailureKind.Write : RecordingFailureKind.Open, ex.Message));
            }
            finally
            {
                _ready.TrySetResult();
                Task cancellation;
                lock (_cancellationGate)
                {
                    _writerFinished = true;
                    cancellation = _cancellation;
                }
                // CancelAsync callbacks finish before disposal; completion callers never own the live token.
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception ex) { TerminalLogger.Warning($"[PtyRecorder] Cancellation callback failed: {ex.Message}"); }
                finally { _cts.Dispose(); }
            }
        }

        public void Dispose()
        {
            // Cleanup is best-effort; explicit save callers await CompleteAsync to report errors.
            try { CompleteAsync().GetAwaiter().GetResult(); }
            catch (RecordingException) { /* Failure is already exposed; explicit saves use CompleteAsync. */ }
            GC.SuppressFinalize(this);
        }
    }
}
