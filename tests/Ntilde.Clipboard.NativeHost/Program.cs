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

    [STAThread]
    public static int Main(string[] args)
    {
        // CI provides a fresh OS clipboard; never default to a developer's desktop.
        if (Environment.GetEnvironmentVariable("CI") != "true" || args.Length != 2 || args[0] != "--isolated-desktop")
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
    private readonly ConcurrentQueue<string> _replies = new();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public async Task RunAsync()
    {
        string fixtureJson = await File.ReadAllTextAsync(Path.Combine(root, "fixture.json"), CancellationToken.None);
        var fixture = JsonSerializer.Deserialize(fixtureJson, QaJson.Default.DictionaryStringString)
            ?? throw new InvalidDataException("Missing fixture.");
        var values = fixture.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value));
        var adapter = new AvaloniaKittyClipboard(() => window.Clipboard, (_, _) => Task.FromResult(false));
        using var session = new KittyClipboardSession(adapter, reply => { _replies.Enqueue(reply); return true; },
            work => Task.Run(() => Dispatcher.UIThread.InvokeAsync(work), CancellationToken.None), () => true, () => true);

        session.Accept("type=write:id=qa-write");
        foreach (var (mime, bytes) in values)
        {
            if (mime == "application/x-ntilde-qa-alias")
            {
                session.Accept("type=walias:mime=" + KittyClipboardPacket.Encode("application/x-ntilde-qa") + ";" + KittyClipboardPacket.Encode(mime));
                continue;
            }
            session.Accept("type=wdata:mime=" + KittyClipboardPacket.Encode(mime) + ";" + Convert.ToBase64String(bytes));
        }
        session.Accept("type=wdata");
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=DONE:id=qa-write", StringComparison.Ordinal)));
        Signal("host-write.ready");
        // Runner allows 25 s for peer read + 25 s for peer write; this handoff covers both.
        await WaitUntilAsync(() => File.Exists(Path.Combine(root, "peer-write.ready")), TimeSpan.FromSeconds(55));

        _replies.Clear();
        session.Accept("type=read:id=denied;" + KittyClipboardPacket.Encode("text/plain"));
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=EPERM:id=denied", StringComparison.Ordinal)));
        if (_replies.Any(r => r.Contains("status=DATA", StringComparison.Ordinal))) throw new InvalidDataException("Denied read emitted data.");

        _replies.Clear();
        await session.NotifyPasteAsync();
        string notification = _replies.First(r => r.Contains(":pw=", StringComparison.Ordinal));
        string password = notification.Split(":pw=", StringSplitOptions.None)[1][..^2];
        _replies.Clear();
        session.Accept("type=read:id=qa-read:pw=" + password + ":name=" + KittyClipboardPacket.Encode("Paste event") + ";" + KittyClipboardPacket.Encode(string.Join(' ', values.Keys)));
        await WaitUntilAsync(() => _replies.Any(r => r.Contains("status=DONE:id=qa-read", StringComparison.Ordinal)));
        string peerJson = await File.ReadAllTextAsync(Path.Combine(root, "peer-fixture.json"), CancellationToken.None);
        var peerFixture = JsonSerializer.Deserialize(peerJson, QaJson.Default.DictionaryStringString)
            ?? throw new InvalidDataException("Missing native peer fixture.");
        VerifyRead(peerFixture.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value)));
        Signal("host-read.verified");
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
            else if (mime.StartsWith("application/x-ntilde-qa", StringComparison.Ordinal))
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
