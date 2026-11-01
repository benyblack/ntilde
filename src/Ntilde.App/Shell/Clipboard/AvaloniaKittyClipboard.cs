using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using System.Text;

namespace Ntilde.Shell.Clipboard;

/// <summary>UI-thread clipboard adapter. It never falls back from primary to the system clipboard.</summary>
internal sealed class AvaloniaKittyClipboard : IKittyClipboard
{
    private readonly Func<Task<IAsyncDataTransfer?>> _read;
    private readonly Func<IAsyncDataTransfer?, Task> _write;
    private readonly Func<Task> _clear;
    private readonly Func<string, CancellationToken, Task<bool>> _confirm;

    public AvaloniaKittyClipboard(Func<IClipboard?> getClipboard, Func<string, CancellationToken, Task<bool>> confirm)
        : this(() => Require(getClipboard).TryGetDataAsync(), data => Require(getClipboard).SetDataAsync(data),
            () => Require(getClipboard).ClearAsync(), confirm)
    { }

    internal AvaloniaKittyClipboard(Func<Task<IAsyncDataTransfer?>> read, Func<IAsyncDataTransfer?, Task> write,
        Func<Task> clear, Func<string, CancellationToken, Task<bool>> confirm)
    {
        _read = read; _write = write; _clear = clear; _confirm = confirm;
    }

    private static IClipboard Require(Func<IClipboard?> get) => get() ?? throw new NotSupportedException("No clipboard is available.");
    public bool Available => true; // TopLevel is resolved only on the UI thread, when an operation runs.

    public async Task<IKittyClipboardSnapshot> OpenReadAsync()
    {
        var data = await _read();
        try { return new Snapshot(data); }
        catch { data?.Dispose(); throw; }
    }

    private sealed class Snapshot : IKittyClipboardSnapshot
    {
        private IAsyncDataTransfer? _data;
        private readonly Dictionary<string, DataFormat[]> _formats;
        public IReadOnlyList<string> Types { get; }

        public Snapshot(IAsyncDataTransfer? data)
        {
            _data = data;
            _formats = (data?.Formats ?? []).Select(format => (Mime: ToMime(format), Format: format))
                .Where(pair => pair.Mime != null).GroupBy(pair => pair.Mime ?? "", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Format).ToArray(), StringComparer.Ordinal);
            Types = _formats.Keys.ToArray();
        }

        public async Task<byte[]?> ReadAsync(string mime, int maxBytes = Ntilde.VT.KittyClipboardPacket.MaxDataBytes)
        {
            if (_data == null || !_formats.TryGetValue(mime, out var formats)) return null;
            foreach (var format in formats)
            {
                foreach (var item in _data.Items)
                {
                    object? value = await item.TryGetRawAsync(format);
                    byte[]? bytes = ToBytes(value, maxBytes);
                    if (bytes != null) return bytes;
                }
            }
            return null;
        }

        private static byte[]? ToBytes(object? value, int maxBytes)
        {
            switch (value)
            {
                case byte[] bytes:
                    EnsureSize(bytes.Length, maxBytes);
                    return bytes;
                case string text:
                    EnsureSize(Encoding.UTF8.GetByteCount(text), maxBytes);
                    return Encoding.UTF8.GetBytes(text);
                case Bitmap bitmap:
                    EnsureSize((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4, maxBytes);
                    using (var stream = new ClipboardReadLimitStream(maxBytes))
                    {
                        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                        return stream.ToArray();
                    }
                default: return null;
            }
        }

        private static void EnsureSize(long size, int maxBytes)
        {
            if (size > maxBytes) throw new IOException("Clipboard item exceeds the remaining read budget.");
        }

        public void Dispose()
        {
            _data?.Dispose();
            _data = null;
        }
    }

    public async Task WriteAsync(IReadOnlyDictionary<string, byte[]> data)
    {
        if (data.Count == 0) { await _clear(); return; }
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        foreach (var (mime, bytes) in data)
        {
            item.Set(DataFormat.CreateBytesPlatformFormat(ClipboardMimeNames.ToPlatform(mime)), bytes);
            if (mime == "text/plain") item.SetText(Encoding.UTF8.GetString(bytes));
        }
        transfer.Add(item);
        try { await _write(transfer); }
        catch { ((IDisposable)transfer).Dispose(); throw; } // ownership passes to Avalonia only on success
    }

    public Task<bool> ConfirmReadAsync(string name, CancellationToken cancellation) => _confirm(name, cancellation);

    internal static string? ToMime(DataFormat format)
    {
        if (format == DataFormat.Text) return "text/plain";
        if (format == DataFormat.Bitmap) return "image/png";
        if (format.Kind != DataFormatKind.Platform) return null;
        string? mime = ClipboardMimeNames.ToMime(format.Identifier);
        return mime != null && Ntilde.VT.KittyClipboardPacket.IsMime(mime) ? mime : null;
    }
}
