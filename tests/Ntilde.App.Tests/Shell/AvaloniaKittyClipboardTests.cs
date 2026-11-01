using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Ntilde.Shell.Clipboard;
using Xunit;

namespace Ntilde.Tests.Shell;

public class AvaloniaKittyClipboardTests
{
    // This backend contains only test bytes; no test obtains a TopLevel/system clipboard.
    private sealed class Clipboard : IDisposable
    {
        private IAsyncDataTransfer? _stored;
        public int Clears, Acquisitions;
        public Task SetDataAsync(IAsyncDataTransfer? dataTransfer) { _stored?.Dispose(); _stored = dataTransfer; return Task.CompletedTask; }
        public Task ClearAsync() { Clears++; _stored?.Dispose(); _stored = null; return Task.CompletedTask; }
        public Task FlushAsync() => Task.CompletedTask;
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => Task.FromResult(_stored);
        public async Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            Acquisitions++;
            if (_stored == null) return null;
            var copy = new DataTransfer();
            var target = new DataTransferItem();
            foreach (var item in _stored.Items)
                foreach (var format in item.Formats)
                {
                    object? value = await item.TryGetRawAsync(format);
                    if (format is DataFormat<byte[]> bytes && value is byte[] data) target.Set(bytes, data);
                    if (format is DataFormat<string> text && value is string s) target.Set(text, s);
                    if (format == DataFormat.Bitmap && value is Bitmap bitmap) target.Set(DataFormat.Bitmap, bitmap);
                }
            copy.Add(target);
            return copy;
        }
        public void Dispose() => _stored?.Dispose();
    }

    [Fact]
    public void Encoder_output_limit_is_checked_before_the_buffer_grows()
    {
        using var stream = new ClipboardReadLimitStream(3);
        stream.Write(new byte[] { 1, 2, 3 });
        Assert.Throws<IOException>(() => stream.WriteByte(4));
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.ToArray());
    }

    [AvaloniaTheory]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task Bitmap_reads_encode_PNG_or_reject_the_pixel_budget_and_dispose_the_value(int budget)
    {
        using var clipboard = new Clipboard();
        using var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(DataFormat.Bitmap, bitmap); transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
        var adapter = new AvaloniaKittyClipboard(clipboard.TryGetDataAsync, clipboard.SetDataAsync, clipboard.ClearAsync, (_, _) => Task.FromResult(false));
        using var snapshot = await adapter.OpenReadAsync();
        Assert.Contains("image/png", snapshot.Types);
        if (budget == 3) await Assert.ThrowsAsync<IOException>(() => snapshot.ReadAsync("image/png", maxBytes: budget));
        else
        {
            byte[] png = Assert.IsType<byte[]>(await snapshot.ReadAsync("image/png", maxBytes: budget));
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            using var decoded = new Bitmap(new MemoryStream(png));
            Assert.Equal(new PixelSize(1, 1), decoded.PixelSize);
        }
        using var output = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() => bitmap.Save(output, PngBitmapEncoderOptions.Default));
    }

    [Fact]
    public async Task Binary_MIME_aliases_and_plain_text_round_trip_through_Avalonia_formats()
    {
        using var clipboard = new Clipboard();
        var adapter = new AvaloniaKittyClipboard(clipboard.TryGetDataAsync, clipboard.SetDataAsync, clipboard.ClearAsync, (_, _) => Task.FromResult(false));
        byte[] binary = [0, 1, 2, 255];
        await adapter.WriteAsync(new Dictionary<string, byte[]>
        {
            ["text/plain"] = System.Text.Encoding.UTF8.GetBytes("héllo"),
            ["image/png"] = binary,
            ["application/x-ntilde-alias"] = binary
        });
        using var snapshot = await adapter.OpenReadAsync();
        var types = snapshot.Types;
        Assert.Equal(3, types.Count);
        Assert.Contains("text/plain", types);
        Assert.Contains("image/png", types);
        Assert.Contains("application/x-ntilde-alias", types);
        Assert.Equal(binary, await snapshot.ReadAsync("image/png"));
        Assert.Equal(binary, await snapshot.ReadAsync("application/x-ntilde-alias"));
        await Assert.ThrowsAsync<IOException>(() => snapshot.ReadAsync("image/png", maxBytes: 3));
        await Assert.ThrowsAsync<IOException>(() => snapshot.ReadAsync("text/plain", maxBytes: 5));
        Assert.Equal("héllo", System.Text.Encoding.UTF8.GetString((await snapshot.ReadAsync("text/plain"))!));
        Assert.Equal(1, clipboard.Acquisitions);
        Assert.Equal(3, snapshot.Types.Count);
        await adapter.WriteAsync(new Dictionary<string, byte[]>());
        Assert.Equal(1, clipboard.Clears);
        using var empty = await adapter.OpenReadAsync();
        Assert.Empty(empty.Types);
        // The read handle retains its original data even after the clipboard is replaced.
        Assert.Equal(binary, await snapshot.ReadAsync("image/png"));
        snapshot.Dispose();
        Assert.Null(await snapshot.ReadAsync("image/png"));
    }
}
