using System.Text;
using Ntilde.VT;

namespace Ntilde.Shell.Clipboard;

internal interface IKittyClipboard
{
    bool Available { get; }
    Task<IKittyClipboardSnapshot> OpenReadAsync();
    Task WriteAsync(IReadOnlyDictionary<string, byte[]> data);
    Task<bool> ConfirmReadAsync(string name, CancellationToken cancellation);
}

internal interface IKittyClipboardSnapshot : IDisposable
{
    IReadOnlyList<string> Types { get; }
    Task<byte[]?> ReadAsync(string mime, int maxBytes = KittyClipboardPacket.MaxDataBytes);
}

/// <summary>
/// One TTY's bounded clipboard transactions and paste grant. Accept runs on the output thread;
/// clipboard operations and prompts run through the UI scheduler. Neither is run under the VT lock.
/// </summary>
internal sealed class KittyClipboardSession : IDisposable
{
    private const string WriteOperation = "write";
    private const string ReadOperation = "read";
    private const string InvalidPacket = "EINVAL";
    private const string Busy = "EBUSY";
    private const string NotImplemented = "ENOSYS";
    private const string PermissionDenied = "EPERM";
    private const string TooLarge = "EFBIG";

    private static readonly SemaphoreSlim ClipboardGate = new(1, 1);
    private readonly object _sync = new();
    private readonly IKittyClipboard _clipboard;
    private readonly ClipboardReplyWriter _replies;
    private readonly Func<Func<Task>, Task> _schedule;
    private readonly Func<bool> _writeAllowed;
    private readonly Func<bool> _alive;
    private readonly TimeProvider _clock;
    private WriteTransfer? _write;
    private bool _busy, _disposed;
    private int _generation;
    private CancellationTokenSource _cancellation = new();
    private string? _pastePassword;
    private DateTimeOffset _pasteExpiry;

    public KittyClipboardSession(IKittyClipboard clipboard, Func<string, bool> reply, Func<Func<Task>, Task> schedule,
        Func<bool> writeAllowed, Func<bool> alive, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? replyDelay = null, TimeSpan? replyTimeout = null)
    {
        _clipboard = clipboard;

        _schedule = schedule;
        _writeAllowed = writeAllowed;
        _alive = alive;
        _clock = clock ?? TimeProvider.System;
        _replies = new ClipboardReplyWriter(reply, alive, _clock, replyDelay, replyTimeout, _sync);
    }

    public void Accept(string data)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var packet = KittyClipboardPacket.Parse(data);
            if (packet == null)
            {
                if (_write != null) Abort(InvalidPacket);
                else if (data.StartsWith("type=write", StringComparison.Ordinal)) Reply(WriteOperation, InvalidPacket, "");
                return;
            }
            if (_write != null && _clock.GetUtcNow() - _write.Started > TimeSpan.FromMinutes(1)) Abort(Busy);
            if (packet.Type == ReadOperation) { Read(packet); return; }
            if (packet.Type == WriteOperation) { StartWrite(packet); return; }
            if (_write is not WriteTransfer transfer) return; // after error, ignore until the next start
            if (packet.Primary) { Abort(NotImplemented); return; }
            ContinueWrite(packet, transfer);
        }
    }

    private void ContinueWrite(KittyClipboardPacket packet, WriteTransfer transfer)
    {
        try
        {
            switch (packet.Type)
            {
                case "wdata":
                    if (packet.Mime.Length == 0 && packet.Payload.Length == 0) FinishWrite();
                    else transfer.Add(packet);
                    break;
                case "walias": transfer.Alias(packet); break;
            }
        }
        catch (InvalidDataException error) { Abort(error.Message); }
    }

    private void StartWrite(KittyClipboardPacket packet)
    {
        _write?.Dispose();
        _write = null;
        string? error = Unavailable(packet.Primary, includeWrite: false);
        if (error == null && !_writeAllowed()) error = PermissionDenied;
        if (error != null) { Reply(WriteOperation, error, packet.Id); return; }
        if (packet.Payload.Length != 0 || !KittyClipboardPacket.TryDecodeText(packet.Password, out _) ||
            !KittyClipboardPacket.TryDecodeText(packet.Name, out _)) { Reply(WriteOperation, InvalidPacket, packet.Id); return; }
        _write = new WriteTransfer(packet.Id, _clock.GetUtcNow());
    }

    private void Abort(string status)
    {
        if (_write == null) return;
        string id = _write.Id;
        _write.Dispose();
        _write = null;
        Reply(WriteOperation, status, id);
    }

    private void FinishWrite()
    {
        if (_write is not WriteTransfer transfer) return;
        var values = transfer.Finish();
        string id = transfer.Id;
        transfer.Dispose();
        _write = null;
        _busy = true;
        int generation = _generation;
        _ = _schedule(() => PerformAsync(WriteOperation, id, generation, async () =>
        {
            if (!_writeAllowed()) return PermissionDenied;
            await _clipboard.WriteAsync(values);
            return "DONE";
        }));
    }

    private string? Unavailable(bool primary, bool includeWrite)
    {
        if (primary || !_clipboard.Available) return NotImplemented;
        if (_busy || (includeWrite && _write != null)) return Busy;
        return null;
    }

    private async Task PerformAsync(string type, string id, int generation, Func<Task<string?>> action)
    {
        bool gate = false;
        CancellationToken cancellation = CancellationToken.None;
        string? status = null;
        using var completion = new OperationCompletion(() =>
        {
            lock (_sync)
            {
                if (gate) ClipboardGate.Release();
                _busy = false;
            }
        });
        try
        {
            lock (_sync)
            {
                if (!IsAlive(generation)) return;
                cancellation = _cancellation.Token;
            }
            // An immediate try, with no queued waiter to outlive this request.
            gate = await ClipboardGate.WaitAsync(0, CancellationToken.None);
            status = gate ? await action() : Busy;
        }
        catch (Exception error)
        {
            status = ErrorStatus(type, error);
        }
        // The terminal packet's admission and slot release share the same lock. A
        // fast child cannot observe DONE while this request still owns either gate.
        if (status != null) await SendTerminalAsync(type, status, id, generation, completion.Dispose, cancellation);
    }

    private async Task SendTerminalAsync(string type, string status, string id, int generation, Action completed, CancellationToken cancellation)
    {
        try
        {
            await Task.Run(async () =>
            {
                if (!IsAlive(generation)) return;
                bool sent = await _replies.SendAsync(KittyClipboardPacket.Response(type, status, id), cancellation, completed);
                if (!sent && status == "DONE" && IsAlive(generation))
                    await _replies.SendAsync(KittyClipboardPacket.Response(type, TransportFailure(type), id), cancellation, completed);
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Reset/disposal canceled this request after its native resources were released.
        }
    }

    private static string ErrorStatus(string type, Exception error)
    {
        if (error is NotSupportedException) return NotImplemented;
        if (error is UnauthorizedAccessException) return PermissionDenied;
        return TransportFailure(type);
    }

    private static string TransportFailure(string type) => type == WriteOperation ? "EIO" : Busy;

    private void Read(KittyClipboardPacket packet)
    {
        if (!KittyClipboardPacket.TryDecodeText(packet.Payload, out string requested)) return;
        string[] types = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        bool listing = requested == ".";
        if (types.Length == 0 || types.Length > KittyClipboardPacket.MaxTypes || (!listing && types.Any(t => !KittyClipboardPacket.IsMime(t)))) return;
        if (!KittyClipboardPacket.TryDecodeText(packet.Name, out string name) || !KittyClipboardPacket.TryDecodeText(packet.Password, out string password)) return;
        string? error = Unavailable(packet.Primary, includeWrite: true);
        if (error != null) { Reply(ReadOperation, error, packet.Id); return; }
        bool granted = !listing && name.Length > 0 && password.Length > 0 && password == _pastePassword && _clock.GetUtcNow() < _pasteExpiry;
        if (granted) _pastePassword = null; // consume before any await, once on this TTY and location
        _busy = true;
        int generation = _generation;
        CancellationToken cancellation = _cancellation.Token;
        _ = _schedule(() => ReadAsync(packet.Id, types, listing, name, granted, generation, cancellation));
    }

    private Task ReadAsync(string id, string[] types, bool listing, string name, bool granted, int generation, CancellationToken cancellation)
    {
        return PerformAsync(ReadOperation, id, generation, async () =>
        {
            if (!listing && !granted && !await _clipboard.ConfirmReadAsync(SafeName(name), cancellation))
            {
                return PermissionDenied;
            }
            if (!IsAlive(generation)) return null;
            var values = await CollectReadValuesAsync(types, listing, generation);
            if (values == null) return null;
            // Clipboard API calls stay UI-affine; encoding and PTY transmission do not.
            bool sent = await Task.Run(() => SendDataAsync(id, values, generation, cancellation), CancellationToken.None);
            return sent ? "DONE" : null;
        });
    }

    private async Task<Dictionary<string, byte[]>?> CollectReadValuesAsync(string[] types, bool listing, int generation)
    {
        using var snapshot = await _clipboard.OpenReadAsync();
        var available = snapshot.Types.Where(KittyClipboardPacket.IsMime).Distinct(StringComparer.Ordinal).Take(KittyClipboardPacket.MaxTypes).ToArray();
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (listing)
        {
            values["."] = Encoding.UTF8.GetBytes(string.Join(' ', available));
            return values;
        }
        long total = 0;
        foreach (string mime in types.Where(available.Contains))
        {
            if (!IsAlive(generation)) return null;
            byte[]? bytes = await snapshot.ReadAsync(mime, KittyClipboardPacket.MaxDataBytes - (int)total);
            if (bytes == null) continue;
            total += bytes.Length;
            if (total > KittyClipboardPacket.MaxDataBytes)
            {
                throw new IOException("Clipboard read exceeds the decoded-byte limit.");
            }
            values[mime] = bytes;
        }
        return values;
    }

    public Task NotifyPasteAsync()
    {
        int generation;
        CancellationToken cancellation;
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            if (_busy || _write != null)
                return Task.Run(() => Reply(ReadOperation, Busy, ""), CancellationToken.None);
            _busy = true;
            generation = _generation;
            cancellation = _cancellation.Token;
        }
        return PerformAsync(ReadOperation, "", generation, async () =>
        {
            using var snapshot = await _clipboard.OpenReadAsync();
            var types = snapshot.Types;
            string password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            lock (_sync)
            {
                if (!IsAlive(generation)) return null;
                _pastePassword = password;
                _pasteExpiry = _clock.GetUtcNow().AddSeconds(15);
            }
            var values = new Dictionary<string, byte[]>
            {
                ["."] = Encoding.UTF8.GetBytes(string.Join(' ', types.Where(KittyClipboardPacket.IsMime).Distinct().Take(KittyClipboardPacket.MaxTypes)))
            };
            bool sent = await Task.Run(() => SendDataAsync("", values, generation, cancellation, ":pw=" + KittyClipboardPacket.Encode(password)), CancellationToken.None);
            return sent ? "DONE" : null;
        });
    }

    private async Task<bool> SendDataAsync(string id, IReadOnlyDictionary<string, byte[]> data, int generation, CancellationToken cancellation, string metadata = "")
    {
        if (!IsAlive(generation)) return false;
        await SendReadPacketAsync(KittyClipboardPacket.Response(ReadOperation, "OK", id, metadata), cancellation);
        foreach (var (mime, bytes) in data)
        {
            for (int offset = 0; offset < bytes.Length || offset == 0; offset += 4096)
            {
                if (!IsAlive(generation)) return false;
                int count = Math.Min(4096, bytes.Length - offset);
                await SendReadPacketAsync(KittyClipboardPacket.Response(ReadOperation, "DATA", id, ":mime=" + KittyClipboardPacket.Encode(mime), Convert.ToBase64String(bytes, offset, count)), cancellation);
            }
        }
        return IsAlive(generation);
    }

    private async Task SendReadPacketAsync(string response, CancellationToken cancellation)
    {
        if (!await _replies.SendAsync(response, cancellation)) throw new IOException("Clipboard reply queue remained unavailable.");
    }

    private static string SafeName(string name) => new(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).Take(80).ToArray());
    private void Reply(string type, string status, string id) => _replies.TrySend(KittyClipboardPacket.Response(type, status, id));
    private bool IsAlive(int generation) { lock (_sync) return !_disposed && generation == _generation && _alive(); }
    public void Reset()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _generation++;
            _cancellation.Cancel();
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
            _write?.Dispose();
            _write = null;
            _pastePassword = null;
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
            _cancellation.Dispose();
        }
    }

    private sealed class OperationCompletion(Action release) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) release();
        }
    }

    private sealed class WriteTransfer(string id, DateTimeOffset started) : IDisposable
    {
        public string Id { get; } = id;
        public DateTimeOffset Started { get; } = started;
        private readonly Dictionary<string, MemoryStream> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private string? _current;
        private string _carry = "";
        private bool _padded;
        private int _size;

        public void Add(KittyClipboardPacket packet)
        {
            if (!KittyClipboardPacket.TryDecodeText(packet.Mime, out string mime) || !KittyClipboardPacket.IsMime(mime)) throw new InvalidDataException(InvalidPacket);
            if (mime != _current) StartMime(mime);
            if (_padded && packet.Payload.Length > 0) throw new InvalidDataException(InvalidPacket);
            string encoded = _carry + packet.Payload;
            if (encoded.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))) throw new InvalidDataException(InvalidPacket);
            int full = encoded.Length / 4 * 4;
            if (!KittyClipboardPacket.TryDecode(encoded[..full], out byte[] bytes)) throw new InvalidDataException(InvalidPacket);
            if (bytes.Length > KittyClipboardPacket.MaxDataBytes - _size) throw new InvalidDataException(TooLarge);
            _values[mime].Write(bytes);
            _size += bytes.Length;
            if (packet.Payload.Length > 0) _padded = encoded.AsSpan(0, full).Contains('=');
            _carry = encoded[full..];
            if (_padded && _carry.Length != 0) throw new InvalidDataException(InvalidPacket);
        }

        private void StartMime(string mime)
        {
            FinishType();
            if (_values.ContainsKey(mime) || _aliases.ContainsKey(mime)) throw new InvalidDataException(InvalidPacket);
            if (_values.Count + _aliases.Count >= KittyClipboardPacket.MaxTypes) throw new InvalidDataException(TooLarge);
            _values[mime] = new MemoryStream();
            _current = mime;
            _padded = false;
        }

        private void FinishType() { if (_carry.Length != 0) throw new InvalidDataException(InvalidPacket); }

        public void Alias(KittyClipboardPacket packet)
        {
            if (!KittyClipboardPacket.TryDecodeText(packet.Mime, out string target) || !KittyClipboardPacket.IsMime(target) ||
                !KittyClipboardPacket.TryDecodeText(packet.Payload, out string aliases) || string.IsNullOrWhiteSpace(aliases)) throw new InvalidDataException(InvalidPacket);
            foreach (string alias in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!KittyClipboardPacket.IsMime(alias) || alias == target || _values.ContainsKey(alias) || !_aliases.TryAdd(alias, target)) throw new InvalidDataException(InvalidPacket);
                if (_values.Count + _aliases.Count > KittyClipboardPacket.MaxTypes) throw new InvalidDataException(TooLarge);
            }
        }

        public Dictionary<string, byte[]> Finish()
        {
            FinishType();
            // Native clipboards may materialize every alias separately. Bound that expansion
            // before flattening buffers or passing any data to the OS, while allowing a full
            // 64 MiB payload plus one alias.
            long materialized = _size;
            foreach (string target in _aliases.Values)
            {
                if (!_values.TryGetValue(target, out var stream)) throw new InvalidDataException(InvalidPacket);
                materialized += stream.Length;
                if (materialized > 2L * KittyClipboardPacket.MaxDataBytes) throw new InvalidDataException(TooLarge);
            }
            var values = _values.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
            foreach (var (alias, target) in _aliases) values[alias] = values[target];
            return values;
        }
        public void Dispose()
        {
            foreach (var stream in _values.Values) stream.Dispose();
            _values.Clear();
        }
    }
}
