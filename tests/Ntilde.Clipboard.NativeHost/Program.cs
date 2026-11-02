using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Ntilde.Shell.Clipboard;
using Ntilde.VT;

namespace Ntilde.ClipboardQa;

internal static class Program
{
    internal static string Root { get; private set; } = "";
    internal static bool Primary => Environment.GetEnvironmentVariable("NTILDE_CLIPBOARD_SELECTION") == "primary";

    [STAThread]
    public static int Main(string[] args)
    {
        // CI provides a fresh OS clipboard; never default to a developer's desktop.
        if (Environment.GetEnvironmentVariable("CI") != "true" || args.Length != 2 || args[0] != "--isolated-desktop" || (Primary && !OperatingSystem.IsLinux()))
        {
            Console.Error.WriteLine("Requires CI=true and --isolated-desktop <private fixture directory>. Do not run on a personal desktop.");
            return 2;
        }
        Root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(Root)) return 2;
        AppBuilder.Configure<ClipboardQaApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
        return Environment.ExitCode;
    }
}

internal sealed class ClipboardQaApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Width = 240, Height = 100, Title = "Ntilde isolated clipboard QA" };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try { await new ClipboardScenario(window, Program.Root).RunAsync(); }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(Program.Root, "error.txt"), error.ToString());
                    Environment.ExitCode = 1;
                }
                finally { desktop.Shutdown(Environment.ExitCode); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ClipboardScenario(Window window, string root)
{
    private const string TextMime = "text/plain";
    private const string BinaryMime = "application/x-ntilde-qa";
    private const string AliasMime = "application/x-ntilde-qa-alias";
    private readonly ConcurrentQueue<string> _replies = new();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public async Task RunAsync()
    {
        string fixtureJson = await File.ReadAllTextAsync(Path.Combine(root, "fixture.json"), CancellationToken.None);
        var fixture = JsonSerializer.Deserialize(fixtureJson, QaJson.Default.DictionaryStringString)
            ?? throw new InvalidDataException("Missing fixture.");
        var values = fixture.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value));
        var adapter = new AvaloniaKittyClipboard(() => window.Clipboard, (_, _) => Task.FromResult(ClipboardReadConsent.Deny));
        if (Program.Primary) XcbPrimaryClipboard.TraceForTest = message => File.AppendAllText(Path.Combine(root, "primary.log"), message + Environment.NewLine);
        bool allowPrimaryRead = false;
        string location = Program.Primary ? ":loc=primary" : "";
        const string sentinel = "Ntilde isolated default clipboard sentinel";
        if (Program.Primary) await adapter.WriteAsync(new Dictionary<string, byte[]> { [TextMime] = System.Text.Encoding.UTF8.GetBytes(sentinel) });
        var primary = new XcbPrimaryClipboard((_, _) => Task.FromResult(allowPrimaryRead ? ClipboardReadConsent.AllowOnce : ClipboardReadConsent.Deny));
        using var session = new KittyClipboardSession(new ClipboardLocations(adapter, primary), reply => { _replies.Enqueue(reply); return true; },
            work => Task.Run(() => Dispatcher.UIThread.InvokeAsync(work), CancellationToken.None), () => true, () => true);

        WritePackets(session, values, location);
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=DONE:id=qa-write", StringComparison.Ordinal)));
        var beforePeer = (Sent: 0, Received: 0);
        if (Program.Primary) beforePeer = await VerifyPrimarySelfReadAsync(primary, values);
        Signal("host-write.ready");
        // Runner allows 25 s for peer read + 25 s for peer write; this handoff covers both.
        await WaitUntilAsync(() => File.Exists(Path.Combine(root, "peer-write.ready")), TimeSpan.FromSeconds(55));

        if (Program.Primary) await VerifyPrimaryListingAsync(session, primary);
        _replies.Clear();
        session.Accept("type=read:id=denied" + location + ";" + KittyClipboardPacket.Encode(TextMime));
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=EPERM:id=denied", StringComparison.Ordinal)));
        if (_replies.Any(r => r.Contains("status=DATA", StringComparison.Ordinal))) throw new InvalidDataException("Denied read emitted data.");

        _replies.Clear();
        if (Program.Primary)
        {
            allowPrimaryRead = true;
            session.Accept("type=read:id=qa-read" + location + ";" + KittyClipboardPacket.Encode(string.Join(' ', values.Keys)));
        }
        else
        {
            await session.NotifyPasteAsync();
            string notification = _replies.First(r => r.Contains(":pw=", StringComparison.Ordinal));
            string password = notification.Split(":pw=", StringSplitOptions.None)[1][..^2];
            _replies.Clear();
            session.Accept("type=read:id=qa-read:pw=" + password + ":name=" + KittyClipboardPacket.Encode("Paste event") + ";" + KittyClipboardPacket.Encode(string.Join(' ', values.Keys)));
        }
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=DONE:id=qa-read", StringComparison.Ordinal)));
        string peerJson = await File.ReadAllTextAsync(Path.Combine(root, "peer-fixture.json"), CancellationToken.None);
        var peerFixture = JsonSerializer.Deserialize(peerJson, QaJson.Default.DictionaryStringString)
            ?? throw new InvalidDataException("Missing native peer fixture.");
        var expected = peerFixture.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value));
        VerifyRead(expected);
        if (Program.Primary) await VerifyPrimaryFreezeAsync(primary, expected);
        if (Program.Primary) await VerifyPrimaryResultAsync(session, primary, adapter, beforePeer, sentinel);
        Signal("host-read.verified");
    }

    private async Task VerifyPrimaryListingAsync(KittyClipboardSession session, XcbPrimaryClipboard primary)
    {
        _replies.Clear(); session.Accept("type=read:loc=primary:id=listing;" + KittyClipboardPacket.Encode("."));
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=DONE:id=listing", StringComparison.Ordinal)));
        string prefix = ":mime=" + KittyClipboardPacket.Encode(".") + ";";
        byte[] payload = _replies.Where(r => r.Contains(prefix, StringComparison.Ordinal))
            .SelectMany(r => Convert.FromBase64String(r[(r.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..^2])).ToArray();
        string[] types = System.Text.Encoding.UTF8.GetString(payload).Split(' ');
        if (await primary.AdvertisedTargetCountForTestAsync() <= 144) throw new InvalidDataException("TARGETS fixture did not exceed the old raw atom limit.");
        if (!types.Contains(TextMime) || types.Length > KittyClipboardPacket.MaxTypes) throw new InvalidDataException("Large TARGETS listing lost text or exceeded the MIME cap.");
    }

    private async Task VerifyPrimaryFreezeAsync(XcbPrimaryClipboard primary, Dictionary<string, byte[]> expected)
    {
        uint owner = await primary.OwnerWindowForTestAsync();
        using var snapshot = await primary.OpenReadAsync(expected.Keys.ToArray());
        Signal("peer-republish.request");
        await WaitUntilAsync(() => File.Exists(Path.Combine(root, "peer-republish.ready")));
        if (await primary.OwnerWindowForTestAsync() != owner) throw new InvalidDataException("Snapshot fixture did not republish on the same owner window.");
        foreach (var (mime, bytes) in expected)
        {
            byte[]? retained = await snapshot.ReadAsync(mime);
            if (retained == null || !retained.SequenceEqual(bytes)) throw new InvalidDataException("PRIMARY snapshot changed after republish: " + mime);
        }
        using var current = await primary.OpenReadAsync([TextMime]);
        byte[]? fresh = await current.ReadAsync(TextMime);
        if (fresh == null || fresh.SequenceEqual(expected[TextMime])) throw new InvalidDataException("PRIMARY new snapshot did not observe republished data.");
        Signal("primary-freeze.verified");
    }

    private static async Task<(int Sent, int Received)> VerifyPrimarySelfReadAsync(XcbPrimaryClipboard primary, Dictionary<string, byte[]> values)
    {
        using var own = await primary.OpenReadAsync();
        byte[]? binary = await own.ReadAsync(BinaryMime);
        if (binary == null || !binary.SequenceEqual(values[BinaryMime])) throw new InvalidDataException("PRIMARY self-read did not preserve bytes.");
        var counts = await primary.TransferCountsAsync();
        if (counts.Sent == 0 || counts.Received == 0) throw new InvalidDataException("PRIMARY self-read did not exercise INCR.");
        return counts;
    }

    private async Task VerifyPrimaryResultAsync(KittyClipboardSession session, XcbPrimaryClipboard primary,
        AvaloniaKittyClipboard adapter, (int Sent, int Received) beforePeer, string sentinel)
    {
        _replies.Clear();
        session.Accept("type=write:loc=primary:id=qa-clear"); session.Accept("type=wdata");
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=DONE:id=qa-clear", StringComparison.Ordinal)));
        using var cleared = await primary.OpenReadAsync();
        if (cleared.Types.Count != 0) throw new InvalidDataException("PRIMARY empty write did not clear the peer's selection.");
        var totals = await primary.TransferCountsAsync();
        var counts = (Sent: totals.Sent - beforePeer.Sent, Received: totals.Received - beforePeer.Received);
        if (counts.Sent == 0 || counts.Received == 0) throw new InvalidDataException("PRIMARY qualification did not exercise INCR in both directions: " + counts);
        await File.WriteAllTextAsync(Path.Combine(root, "primary-incr.verified"), "sent=" + counts.Sent + ",received=" + counts.Received, CancellationToken.None);
        using var snapshot = await adapter.OpenReadAsync();
        byte[]? text = await snapshot.ReadAsync(TextMime);
        if (text == null || System.Text.Encoding.UTF8.GetString(text) != sentinel) throw new InvalidDataException("PRIMARY modified the default clipboard.");
    }

    private static void WritePackets(KittyClipboardSession session, Dictionary<string, byte[]> values, string location)
    {
        session.Accept("type=write:id=qa-write" + location);
        foreach (var (mime, bytes) in values)
        {
            if (mime == AliasMime)
            {
                session.Accept("type=walias:mime=" + KittyClipboardPacket.Encode(BinaryMime) + ";" + KittyClipboardPacket.Encode(mime));
                continue;
            }
            for (int offset = 0; offset < bytes.Length || offset == 0; offset += 3072)
                session.Accept("type=wdata:mime=" + KittyClipboardPacket.Encode(mime) + ";" + Convert.ToBase64String(bytes, offset, Math.Min(3072, bytes.Length - offset)));
        }
        session.Accept("type=wdata");
    }

    private void VerifyRead(Dictionary<string, byte[]> expected)
    {
        foreach (var (mime, bytes) in expected)
        {
            string prefix = ":mime=" + KittyClipboardPacket.Encode(mime) + ";";
            byte[] actual = _replies.Where(r => r.Contains(prefix, StringComparison.Ordinal))
                .SelectMany(r => Convert.FromBase64String(r[(r.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..^2])).ToArray();
            if (mime == "image/png")
            {
                // A native bitmap provider may losslessly re-encode PNG compression/metadata.
                using var expectedImage = SkiaSharp.SKBitmap.Decode(bytes);
                using var actualImage = SkiaSharp.SKBitmap.Decode(actual);
                if (actualImage == null || expectedImage == null || actualImage.Width != 1 || actualImage.Height != 1 ||
                    actualImage.GetPixel(0, 0) != expectedImage.GetPixel(0, 0)) throw new InvalidDataException("Native PNG pixels differ.");
            }
            else if (mime.StartsWith(BinaryMime, StringComparison.Ordinal))
            {
                if (!BinaryPayload(actual, OperatingSystem.IsWindows()).SequenceEqual(BinaryPayload(bytes, false))) throw new InvalidDataException("Native binary payload differs.");
            }
            else if (!actual.SequenceEqual(bytes)) throw new InvalidDataException("Native read mismatch: " + mime);
        }
    }

    private static ReadOnlySpan<byte> BinaryPayload(byte[] data, bool allowPadding)
    {
        if (data.Length < 4) throw new InvalidDataException("Missing binary fixture length.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (length > data.Length - 4 || (!allowPadding && length != data.Length - 4)) throw new InvalidDataException("Invalid binary fixture length.");
        return data.AsSpan(4, (int)length);
    }

    private static async Task WaitUntilAsync(Func<bool> ready, TimeSpan? deadline = null)
    {
        using var timeout = new CancellationTokenSource(deadline ?? Deadline);
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private void Signal(string name) => File.WriteAllText(Path.Combine(root, name), "ok");
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class QaJson : JsonSerializerContext;
