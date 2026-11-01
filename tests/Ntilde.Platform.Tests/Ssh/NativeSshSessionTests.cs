using System.Collections.Concurrent;
using Ntilde.Pty;
using System.Linq;
using System.Text;
using System.Text.Json;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Sessions;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeSshSessionTests
{
    private static readonly string[] EuroSignOnly = ["\u20AC"];
    private static readonly int[] ExitCode23Only = [23];
    private static readonly string[] HelloOnly = ["hello"];

    /// <summary>The App routing tests produce these frames; the managed SSH session must transmit them unchanged.</summary>
    [Theory]
    [InlineData("\x1b[97:65;6u")]
    [InlineData("\x1b[1089::99;5u")]
    [InlineData("\x1b[97::113;1:3u")]
    public void Alternate_keyboard_frames_reach_the_native_write_boundary_unchanged(string frame)
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.SendInput(frame);
        Assert.Equal(Encoding.UTF8.GetBytes(frame), Assert.Single(interop.Writes));
        Assert.Empty(interop.DeviceReplies);
    }

    [Fact]
    public void Device_replies_use_bounded_admission_and_never_fall_back_to_unbounded_input()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var sink = Assert.IsAssignableFrom<ITerminalDeviceReplySink>(session);
        Assert.True(sink.TrySendDeviceReply("reply"));
        Assert.Equal("reply", Encoding.UTF8.GetString(Assert.Single(interop.DeviceReplies)));
        interop.AcceptDeviceReplies = false;
        Assert.False(sink.TrySendDeviceReply("rejected"));
        Assert.Empty(interop.Writes);
        session.SendInput("keyboard");
        Assert.Equal("keyboard", Encoding.UTF8.GetString(Assert.Single(interop.Writes)));
        Assert.False(sink.TrySendDeviceReply(new string('x', 65537)));
        Assert.Single(interop.DeviceReplies);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task Manual_recording_excludes_typed_secrets_without_changing_input_delivery()
    {
        string path = Path.GetTempFileName();
        const string secret = "synthetic-native-password-671\r";
        byte[] output = Encoding.UTF8.GetBytes("Password: \r\nauthenticated\r\n");
        try
        {
            var interop = new FakeNativeSshInterop();
            using var session = new NativeSshSession(CreateProfile(), interop: interop);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnOutputReceived += text =>
            {
                if (text.Contains("authenticated", StringComparison.Ordinal)) delivered.TrySetResult();
            };
            session.StartRecording(path);
            session.SendInput(secret);
            Assert.Equal(Encoding.UTF8.GetBytes(secret), Assert.Single(interop.Writes));

            // The fake sends no echo of the secret, as at a password prompt.
            interop.Enqueue(NativeSshEvent.Data(output));
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            session.StopRecording();

            string recording = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(secret.TrimEnd('\r'), recording, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), recording, StringComparison.Ordinal);
            foreach (string line in File.ReadLines(path))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                Assert.NotEqual("input", document.RootElement.GetProperty("type").GetString());
            }

            var captured = new List<byte>();
            await new Ntilde.Replay.ReplayRunner(path).RunAsync(
                onDataCallback: data => { captured.AddRange(data); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(output, captured);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Host_recording_preserves_original_bytes_and_metrics_when_delivery_is_delayed()
    {
        string path = Path.GetTempFileName();
        using var arrived = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var interop = new FakeNativeSshInterop();
            using var session = new NativeSshSession(CreateProfile(), interop: interop);
            var live = new Ntilde.VT.TerminalBuffer(80, 24);
            var parser = new Ntilde.VT.AnsiParser(live);
            int delivered = 0;
            session.SubscribeRecordingOutput((text, data) =>
            {
                if (Volatile.Read(ref delivered) == 0)
                {
                    arrived.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                }
                session.RecordOutputAtHostBoundary(data);
                parser.Process(text);
                Interlocked.Increment(ref delivered);
            });
            session.StartRecording(path);
            byte[] first = Encoding.UTF8.GetBytes("\x1b[24;1H\x1bPq!35~-~\x1b\\").Concat(new byte[] { 0xE2 }).ToArray();
            byte[] second = [0x82, 0xAC, (byte)'Z'];
            interop.Enqueue(NativeSshEvent.Data(first));
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            session.SetRecordingCellMetrics(12, 6);
            parser.SixelCellSizeOverride = new Ntilde.VT.SixelCellSize(12, 6);
            release.Set();
            await WaitUntilAsync(() => Volatile.Read(ref delivered) == 1);
            interop.Enqueue(NativeSshEvent.Data(second));
            await WaitUntilAsync(() => Volatile.Read(ref delivered) == 2);
            session.StopRecording();
            var replay = new Ntilde.VT.TerminalBuffer(80, 24);
            var replayParser = new Ntilde.VT.AnsiParser(replay);
            var decoder = new Ntilde.Pty.Utf8ChunkDecoder();
            var raw = new List<byte>();
            await new Ntilde.Replay.ReplayRunner(path).RunAsync(
                onDataCallback: data =>
                {
                    raw.AddRange(data);
                    char[] chars = new char[Ntilde.Pty.Utf8ChunkDecoder.GetMaxCharCount(data.Length)];
                    int count = decoder.Decode(data, chars);
                    replayParser.Process(new string(chars, 0, count));
                    return Task.CompletedTask;
                },
                onHeaderCallback: header => { Ntilde.Replay.ReplayRunner.ApplyHeaderMetrics(replayParser, header); return Task.CompletedTask; },
                onSixelCellSizeCallback: (w, h) => { replayParser.SixelCellSizeOverride = new Ntilde.VT.SixelCellSize(w, h); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(first.Concat(second), raw);
            Assert.Equal((live.CursorCol, live.CursorRow, live.TotalLines), (replay.CursorCol, replay.CursorRow, replay.TotalLines));
            Assert.Equal(Ntilde.Replay.BufferSnapshot.Capture(live).Lines, Ntilde.Replay.BufferSnapshot.Capture(replay).Lines);
        }
        finally { release.Set(); File.Delete(path); }
    }

    [Fact]
    public async Task Host_recording_preserves_session_error_after_incomplete_utf8()
    {
        string path = Path.GetTempFileName();
        try
        {
            var interop = new FakeNativeSshInterop();
            using var session = new NativeSshSession(CreateProfile(), interop: interop);
            var live = new Ntilde.VT.TerminalBuffer(80, 24);
            var parser = new Ntilde.VT.AnsiParser(live);
            session.SubscribeRecordingOutput((text, data) =>
            {
                session.RecordOutputAtHostBoundary(data);
                parser.Process(text);
            });
            var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnExit += code => exit.TrySetResult(code);
            session.StartRecording(path);
            byte[] prefix = [(byte)'x', 0xE2, 0x82];
            byte[] message = Encoding.UTF8.GetBytes("auth failed");
            interop.Enqueue(NativeSshEvent.Data(prefix));
            interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Error, message, statusCode: 5));
            Assert.Equal(5, await exit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            session.StopRecording();
            var replay = new Ntilde.VT.TerminalBuffer(80, 24);
            var replayParser = new Ntilde.VT.AnsiParser(replay);
            var decoder = new Ntilde.Pty.Utf8ChunkDecoder();
            var raw = new List<byte>();
            await new Ntilde.Replay.ReplayRunner(path).RunAsync(
                onDataCallback: data =>
                {
                    raw.AddRange(data);
                    char[] chars = new char[Ntilde.Pty.Utf8ChunkDecoder.GetMaxCharCount(data.Length)];
                    int count = decoder.Decode(data, chars);
                    replayParser.Process(new string(chars, 0, count));
                    return Task.CompletedTask;
                },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(prefix.Concat(message).Concat(Encoding.UTF8.GetBytes(Environment.NewLine)), raw);
            Assert.Equal((live.CursorCol, live.CursorRow, live.TotalLines), (replay.CursorCol, replay.CursorRow, replay.TotalLines));
            Assert.Equal(Ntilde.Replay.BufferSnapshot.Capture(live).Lines, Ntilde.Replay.BufferSnapshot.Capture(replay).Lines);
            Assert.Contains("x�auth failed", string.Concat(Ntilde.Replay.BufferSnapshot.Capture(replay).Lines));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Recordings_preserve_initial_and_updated_physical_cell_metrics()
    {
        string path = Path.GetTempFileName();
        try
        {
            var interop = new FakeNativeSshInterop();
            using var session = new NativeSshSession(CreateProfile(), interop: interop);
            session.SetRecordingCellMetrics(12, 6);
            session.StartRecording(path);
            var live = new Ntilde.VT.TerminalBuffer(80, 24);
            var parser = new Ntilde.VT.AnsiParser(live) { SixelCellSizeOverride = new Ntilde.VT.SixelCellSize(12, 6) };
            int delivered = 0;
            session.OnOutputReceived += text => { parser.Process(text); Interlocked.Increment(ref delivered); };
            interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("\x1b[24;1H\x1bPq!35~-~\x1b\\X")));
            await WaitUntilAsync(() => Volatile.Read(ref delivered) == 1);
            session.SetRecordingCellMetrics(12, 12);
            parser.SixelCellSizeOverride = new Ntilde.VT.SixelCellSize(12, 12);
            interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("\x1bPq!35~-~\x1b\\Z")));
            await WaitUntilAsync(() => Volatile.Read(ref delivered) == 2);
            session.StopRecording();
            var replay = new Ntilde.VT.TerminalBuffer(80, 24);
            var replayParser = new Ntilde.VT.AnsiParser(replay);
            await new Ntilde.Replay.ReplayRunner(path).RunAsync(
                onDataCallback: data => { replayParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onHeaderCallback: header => { Ntilde.Replay.ReplayRunner.ApplyHeaderMetrics(replayParser, header); return Task.CompletedTask; },
                onSixelCellSizeCallback: (w, h) => { replayParser.SixelCellSizeOverride = new Ntilde.VT.SixelCellSize(w, h); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal((live.CursorCol, live.CursorRow, live.TotalLines), (replay.CursorCol, replay.CursorRow, replay.TotalLines));
            Assert.Equal(new Ntilde.VT.SixelCellSize(12, 12), replayParser.SixelCellSizeOverride);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OutputBytesAreDecodedIncrementallyAcrossPollEvents()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xE2, 0x82 }));
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xAC }));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        var outputs = new List<string>();
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnOutputReceived += outputs.Add;
        session.OnExit += code => exit.TrySetResult(code);

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(EuroSignOnly, outputs);
    }

    [Fact]
    public async Task EarlyOutput_IsBufferedAndReplayedToLateSubscriber_InOrder()
    {
        // Regression: output produced before the first subscriber attaches must be
        // buffered and replayed in order — not dropped, and never delivered
        // concurrently with the replay (the invocation-lock hardening).
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("first ")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("second")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        // Signal-driven, not clock-driven: once the session has exited, the poll
        // loop has fully processed both Data events (decoded and buffered), so a
        // late output subscriber must receive them via replay — never live. OnExit
        // replays its code to a late subscriber, so subscribing here is race-free.
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);
        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        var outputs = new ConcurrentQueue<string>();
        session.OnOutputReceived += outputs.Enqueue; // late subscriber → replay fires here

        await WaitUntilAsync(() => string.Concat(outputs) == "first second");
        Assert.Equal("first second", string.Concat(outputs));
    }

    [Fact]
    public async Task EarlyOutput_PastTheReplayBound_KeepsTheNewestOutput()
    {
        // #173: the buffer for a late first subscriber had no bound. Three chunks of just under half the
        // bound each: two fit, a third does not, so the first has to go and the last two replay in order.
        int chunk = (Ntilde.Pty.PendingOutputReplay.DefaultMaxChars / 2) - 1;
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes(new string('a', chunk))));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes(new string('b', chunk))));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes(new string('c', chunk))));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);
        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var outputs = new ConcurrentQueue<string>();
        session.OnOutputReceived += outputs.Enqueue;

        string replayed = string.Concat(outputs);
        Assert.Equal(new string('b', chunk) + new string('c', chunk), replayed);
    }

    [Fact]
    public void Constructor_AllowsDynamicForwardProfiles()
    {
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Dynamic,
            BindAddress = "127.0.0.1",
            SourcePort = 0
        });

        using var session = new NativeSshSession(profile, interop: new FakeNativeSshInterop());

        Assert.NotNull(session);
    }

    [Fact]
    public async Task SendInputForwardsUtf8BytesThroughInterop()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.SendInput("echo €\n");

        await WaitUntilAsync(() => interop.Writes.Count > 0);
        Assert.Equal(Encoding.UTF8.GetBytes("echo €\n"), interop.Writes.Single());
    }

    [Fact]
    public async Task ResizeForwardsTerminalDimensions()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.Resize(132, 43);

        await WaitUntilAsync(() => interop.Resizes.Count > 0);
        Assert.Equal((132, 43), interop.Resizes.Single());
    }

    [Fact]
    public async Task ResizeBurst_RecordsLatestDimensionsAsEffectiveInteropIntent()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.Resize(120, 30);
        session.Resize(140, 40);
        session.Resize(160, 50);

        await WaitUntilAsync(() => interop.Resizes.Count >= 3);

        Assert.Equal(3, interop.Resizes.Count);
        Assert.Equal((160, 50), interop.Resizes[^1]);
    }

    [Fact]
    public async Task ExitAndClosedEventsOnlyRaiseOnExitOnce()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.ExitStatus(23));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        var exits = new List<int>();
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnExit += code =>
        {
            exits.Add(code);
            exit.TrySetResult(code);
        };

        Assert.Equal(23, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => interop.CloseCallCount > 0);
        Assert.Equal(ExitCode23Only, exits);
    }

    [Fact]
    public async Task LateOnExitSubscriberReceivesRecordedExitCode()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.ExitStatus(17));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        await WaitUntilAsync(() => interop.CloseCallCount > 0);

        session.OnExit += code => exit.TrySetResult(code);

        Assert.Equal(17, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LateOutputSubscriberReceivesBufferedOutput()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("hello")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));
        var outputs = new List<string>();

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        await WaitUntilAsync(() => interop.CloseCallCount > 0);

        session.OnOutputReceived += outputs.Add;

        Assert.Equal(HelloOnly, outputs);
    }

    [Fact]
    public async Task DisposeClosesNativeHandleAndStopsPollLoop()
    {
        var interop = new FakeNativeSshInterop();
        var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.Dispose();

        await WaitUntilAsync(() => interop.CloseCallCount > 0);
        Assert.Equal(1, interop.CloseCallCount);
    }

    [Fact]
    public async Task Connect_UsesProfileKeepAliveSettingsForNativeSession()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.ServerAliveIntervalSeconds = 15;
        profile.ServerAliveCountMax = 7;

        using var session = new NativeSshSession(profile, interop: interop);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        Assert.Equal(15, interop.LastConnectOptions!.KeepAliveIntervalSeconds);
        Assert.Equal(7, interop.LastConnectOptions.KeepAliveCountMax);
    }

    [Fact]
    public async Task Connect_WithAutoRemoteShellKind_PassesProbeAndSupportedBootstraps()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.RemoteShellKind = RemoteShellKind.Auto;

        using var session = new NativeSshSession(profile, interop: interop);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        Assert.Equal(RemoteShellKind.Auto, interop.LastConnectOptions!.RemoteShellKind);
        Assert.Contains("sh -lc", interop.LastConnectOptions.ShellDetectionCommand, StringComparison.Ordinal);
        Assert.Contains("PROMPT_COMMAND", interop.LastConnectOptions.BashCwdBootstrap, StringComparison.Ordinal);
        Assert.Contains("add-zsh-hook precmd", interop.LastConnectOptions.ZshCwdBootstrap, StringComparison.Ordinal);
        Assert.Contains("fish_prompt", interop.LastConnectOptions.FishCwdBootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_WithExplicitBashRemoteShellKind_SkipsProbeButPassesBootstrap()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.RemoteShellKind = RemoteShellKind.Bash;

        using var session = new NativeSshSession(profile, interop: interop);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        Assert.Equal(RemoteShellKind.Bash, interop.LastConnectOptions!.RemoteShellKind);
        Assert.Null(interop.LastConnectOptions.ShellDetectionCommand);
        Assert.Contains("PROMPT_COMMAND", interop.LastConnectOptions.BashCwdBootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_WithAgentAuthMode_RequestsTheAgentAndIgnoresALeftoverIdentityPath()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.AuthMode = SshAuthMode.Agent;
        // A stale path from before the user switched the profile to agent auth. The explicit
        // choice wins: the file must not be offered.
        profile.IdentityFilePath = "/home/nova/.ssh/id_old";

        using var session = new NativeSshSession(profile, interop: interop);
        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.True(interop.LastConnectOptions!.UseAgent);
        Assert.Null(interop.LastConnectOptions.IdentityFilePath);
    }

    [Fact]
    public async Task Connect_WithIdentityFileAuthMode_DisablesTheAgent()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.AuthMode = SshAuthMode.IdentityFile;
        profile.IdentityFilePath = "/home/nova/.ssh/id_ed25519";

        using var session = new NativeSshSession(profile, interop: interop);
        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        // The same contract OpenSSH gives IdentitiesOnly: an explicit identity file means the
        // agent's keys are not offered.
        Assert.False(interop.LastConnectOptions!.UseAgent);
        Assert.Equal("/home/nova/.ssh/id_ed25519", interop.LastConnectOptions.IdentityFilePath);
    }

    [Fact]
    public async Task Connect_WithDefaultAuthMode_OffersTheAgentAndTheIdentityFile()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.IdentityFilePath = "/home/nova/.ssh/id_ed25519";

        using var session = new NativeSshSession(profile, interop: interop);
        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        // Default expresses no preference, so it gets both: the configured file first (it
        // predates agent support, so profiles that authenticated via file keep working even
        // against an agent full of unrelated keys), then the agent.
        Assert.True(interop.LastConnectOptions!.UseAgent);
        Assert.Equal("/home/nova/.ssh/id_ed25519", interop.LastConnectOptions.IdentityFilePath);
    }

    [Fact]
    public void Connect_WithMuxEnabledButNotControlMasterAuto_WarnsInTheTerminal()
    {
        // The native backend shares connections the way ControlMaster auto does. Without auto,
        // OpenSSH only joins a master another client made, which has no native equivalent: the tab
        // connects on its own, but never silently — the no-silent-degradation contract — and the
        // warning is buffered, so even a subscriber attaching later sees it.
        SshProfile profile = CreateProfile();
        profile.MuxOptions.Enabled = true;
        profile.MuxOptions.ControlMasterAuto = false;

        var outputs = new List<string>();
        using var session = new NativeSshSession(profile, interop: new FakeNativeSshInterop());
        session.OnOutputReceived += outputs.Add;

        Assert.Contains(outputs, text => text.Contains("multiplexing", StringComparison.OrdinalIgnoreCase)
            && text.Contains("ControlMaster auto", StringComparison.Ordinal));
    }

    [Fact]
    public void Connect_WithExtraSshArgs_WarnsInTheTerminalThatNativeIgnoresThem()
    {
        SshProfile profile = CreateProfile();
        profile.ExtraSshArgs = "-o Compression=yes";

        var outputs = new List<string>();
        using var session = new NativeSshSession(profile, interop: new FakeNativeSshInterop());
        session.OnOutputReceived += outputs.Add;

        Assert.Contains(outputs, text => text.Contains("extra SSH arguments", StringComparison.OrdinalIgnoreCase)
            && text.Contains("ignores", StringComparison.Ordinal));
    }

    [Fact]
    public void Connect_WithNeitherMuxNorExtraArgs_EmitsNoIgnoredSettingsWarning()
    {
        var outputs = new List<string>();
        using var session = new NativeSshSession(CreateProfile(), interop: new FakeNativeSshInterop());
        session.OnOutputReceived += outputs.Add;

        Assert.DoesNotContain(outputs, text => text.Contains("ignores", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsolicitedIncomingForwardChannel_WithNoForwardsConfigured_IsClosedNotLeaked()
    {
        // A profile with no forwards has no NativePortForwardSession, so before this fix the
        // announcement was silently dropped — the channel stayed registered and open on the
        // native side, and a hostile server could grow that without bound. The event must be
        // answered with a close, not ignored.
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.ForwardChannelIncoming(
            41,
            Encoding.UTF8.GetBytes("{\"connectedAddress\":\"localhost\",\"connectedPort\":18080,\"originatorAddress\":\"192.0.2.1\",\"originatorPort\":50000}")));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        await WaitUntilAsync(() => interop.ClosedChannelIds.Contains(41));
    }

    [Fact]
    public async Task RemoteForward_IsRequestedWhenTheConnectedEventArrives()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Remote,
            SourcePort = 18081,
            DestinationHost = "127.0.0.1",
            DestinationPort = 9000
        });

        using var session = new NativeSshSession(profile, interop: interop);

        // Nothing to request until the session exists; the Connected event is what says it does.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(interop.RemoteForwardRequests);

        interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Connected, []));

        await WaitUntilAsync(() => interop.RemoteForwardRequests.Count == 1);
        Assert.Equal(("localhost", 18081), interop.RemoteForwardRequests[0]);
    }

    [Fact]
    public async Task RemoteForward_ARefusedRequestWarnsInTheTerminal()
    {
        // ssh -R prints "Warning: remote port forwarding failed" into the session; the native
        // backend keeps the session alive the same way, so the warning must be equally visible.
        var interop = new FakeNativeSshInterop { RefuseRemoteForwards = true };
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Remote,
            SourcePort = 18082,
            DestinationHost = "127.0.0.1",
            DestinationPort = 9001
        });

        var outputs = new ConcurrentQueue<string>();
        using var session = new NativeSshSession(profile, interop: interop);
        session.OnOutputReceived += outputs.Enqueue;

        interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Connected, []));

        await WaitUntilAsync(() => string.Concat(outputs).Contains("localhost:18082", StringComparison.Ordinal));
        Assert.Contains("Warning", string.Concat(outputs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawOutputCarriesTheWireBytesInOrder_IncludingSplitCodePoints()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xE2, 0x82 }));
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xAC, (byte)'!' }));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var raw = new ConcurrentQueue<byte[]>();
        session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);

        await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC, (byte)'!' }, raw.SelectMany(b => b).ToArray());
    }

    [Fact]
    public async Task RawOutputCarriesSessionWrittenText_SoItMatchesTheStringStream()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Error, Encoding.UTF8.GetBytes("auth failed"), statusCode: 5));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var raw = new ConcurrentQueue<byte[]>();
        session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);

        await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("auth failed" + Environment.NewLine, Encoding.UTF8.GetString(raw.SelectMany(b => b).ToArray()));
    }

    [Fact]
    public async Task SessionTextAfterAnIncompleteCodePoint_DecodesIdenticallyOnTheRawAndStringStreams()
    {
        // The wire ends mid code point, then the session writes its own failure banner. A raw
        // subscriber (the mux) decodes prefix + banner bytes and gets U+FFFD before the banner; the
        // string stream must say exactly the same, or the two terminal states diverge right when
        // the connection fails.
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(new byte[] { (byte)'x', 0xE2, 0x82 }));
        interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Error, Encoding.UTF8.GetBytes("auth failed"), statusCode: 5));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var raw = new ConcurrentQueue<byte[]>();
        var text = new ConcurrentQueue<string>();
        session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
        session.OnOutputReceived += text.Enqueue;
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);

        await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => string.Concat(text).Contains("auth failed", StringComparison.Ordinal));

        string decodedRaw = Encoding.UTF8.GetString(raw.SelectMany(b => b).ToArray());
        Assert.Equal(decodedRaw, string.Concat(text));
        Assert.Contains('�', decodedRaw);
    }

    [Fact]
    public async Task LateRawSubscriberAfterAStringSubscriber_GetsNoRetainedBytes()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var text = new ConcurrentQueue<string>();
        session.OnOutputReceived += text.Enqueue;

        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("early")));
        await WaitUntilAsync(() => string.Concat(text) == "early");
        var raw = new ConcurrentQueue<byte[]>();
        session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("late")));
        await WaitUntilAsync(() => string.Concat(text) == "earlylate");

        Assert.Equal("late", Encoding.UTF8.GetString(raw.SelectMany(b => b).ToArray()));
    }

    [Fact]
    public async Task AnIdlePollLoop_ParksInTheNativeWait_OnAThreadOfItsOwn()
    {
        // #173: the loop slept 25 ms between polls, a latency floor on every idle-to-active wake. The native wait
        // returns the moment the session queues output. It blocks, so the loop holds a thread of its own rather than
        // one of the pool's (the #81 starvation family).
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        await WaitUntilAsync(() => interop.WaitCalls > 0);

        Assert.False(interop.PolledOnPoolThread);
    }

    [Fact]
    public async Task AnOutputSubscriberThatThrows_DoesNotEndTheProcess()
    {
        // Codex review of #655: the loop's failure handling hands its banner to the same subscriber, whose second
        // throw leaves the loop. On Task.Run that faulted a task; on a thread of its own it would end the process.
        var interop = new FakeNativeSshInterop();
        var logged = new ConcurrentQueue<string>();
        using var session = new NativeSshSession(CreateProfile(), log: logged.Enqueue, interop: interop);
        session.OnOutputReceived += _ => throw new InvalidOperationException("subscriber failed");

        interop.Enqueue(NativeSshEvent.Data("x"u8.ToArray()));

        await WaitUntilAsync(() => logged.Any(line => line.Contains("poll thread ended", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AnExitHandlerThatDisposesTheSession_DoesNotWaitForItsOwnPollThread()
    {
        // Exit is raised on the poll thread, and a handler there may dispose the session. The thread cannot join
        // itself, so Dispose must not sit out its two seconds waiting for it.
        var interop = new FakeNativeSshInterop();
        var session = new NativeSshSession(CreateProfile(), interop: interop);
        var disposed = new TaskCompletionSource<(string? Thread, TimeSpan Took)>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += _ =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            session.Dispose();
            disposed.TrySetResult((Thread.CurrentThread.Name, clock.Elapsed));
        };

        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        (string? thread, TimeSpan took) = await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("SshSessionPoll", thread);
        Assert.True(took < TimeSpan.FromSeconds(1), $"Dispose took {took}");
    }

    private static SshProfile CreateProfile()
    {
        return new SshProfile
        {
            Id = Guid.Parse("2f56e099-14f4-4219-9b64-fd16465d84fb"),
            BackendKind = SshBackendKind.Native,
            Host = "native.example",
            User = "nova",
            Port = 22
        };
    }

    // 30 s, not the 2 s this used to allow, and only as a ceiling: the loop returns the moment the
    // predicate holds, and every caller waits for something that must happen, so a passing test
    // never pays for it. Several of those somethings run on the thread pool, not the poll thread -
    // NotifySessionEstablished hands the remote-forward requests to Task.Run on purpose, so a slow
    // request cannot stall the loop answering auth prompts. On a 4-core CI runner the pool starts
    // at four threads and injects more slowly when they are busy, so during the parallel start-up
    // burst of this assembly a queued work item can wait past 2 s.
    // RemoteForward_IsRequestedWhenTheConnectedEventArrives failed exactly so on main (windows,
    // 3.7 s into the run, "Condition was not met before timeout.") with nothing wrong in the code
    // under test. NativePortForwardSessionTests, which drives the same Task.Run path, already
    // allows 30 s.
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    internal sealed class FakeNativeSshInterop : INativeSshInterop
    {
        private readonly ConcurrentQueue<NativeSshEvent> _events = new();
        private int _nextHandle = 1;

        public List<byte[]> Writes { get; } = new();
        public List<byte[]> DeviceReplies { get; } = new();
        public bool AcceptDeviceReplies { get; set; } = true;

        public bool TryWriteDeviceReply(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
        {
            if (!AcceptDeviceReplies) return false;
            DeviceReplies.Add(data.ToArray());
            return true;
        }
        public List<(int Cols, int Rows)> Resizes { get; } = new();
        public int CloseCallCount { get; private set; }
        public NativeSshConnectionOptions? LastConnectOptions { get; private set; }
        public Exception? ResizeException { get; set; }
        public bool RefuseRemoteForwards { get; set; }

        // Written from the poll loop / request task while tests poll them; ConcurrentQueue keeps
        // the reads safe without a lock.
        private readonly ConcurrentQueue<int> _closedChannelIds = new();
        private readonly ConcurrentQueue<(string BindAddress, int Port)> _remoteForwardRequests = new();

        public IReadOnlyList<int> ClosedChannelIds => _closedChannelIds.ToArray();
        public IReadOnlyList<(string BindAddress, int Port)> RemoteForwardRequests => _remoteForwardRequests.ToArray();

        public NovaSshSafeHandle Connect(NativeSshConnectionOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            LastConnectOptions = options;
            return new NovaSshSafeHandle(new IntPtr(_nextHandle++), ownsHandle: false);
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle)
        {
            if (sessionHandle is null || sessionHandle.IsInvalid)
            {
                throw new InvalidOperationException("Unexpected null handle.");
            }

            Interlocked.CompareExchange(ref _polledOnPoolThread, Thread.CurrentThread.IsThreadPoolThread ? 1 : 0, -1);
            return _events.TryDequeue(out NativeSshEvent? nextEvent)
                ? nextEvent
                : null;
        }

        // What the poll loop did: whether it polled on a pool thread (-1 until it polled), and how often it parked.
        private int _polledOnPoolThread = -1;
        private int _waitCalls;

        public bool? PolledOnPoolThread => Volatile.Read(ref _polledOnPoolThread) switch { -1 => null, 0 => false, _ => true };

        public int WaitCalls => Volatile.Read(ref _waitCalls);

        /// <summary>What a wait does; by default what the interface's default does: sleep briefly, then report ready.</summary>
        public Func<TimeSpan, bool>? WaitBehavior { get; set; }

        public bool WaitForEvent(NovaSshSafeHandle sessionHandle, TimeSpan timeout)
        {
            Interlocked.Increment(ref _waitCalls);
            if (WaitBehavior is { } behavior)
            {
                return behavior(timeout);
            }

            Thread.Sleep(timeout < TimeSpan.FromMilliseconds(10) ? timeout : TimeSpan.FromMilliseconds(10));
            return true;
        }

        public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
        {
            Writes.Add(data.ToArray());
        }

        public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows)
        {
            if (ResizeException != null)
            {
                throw ResizeException;
            }

            Resizes.Add((cols, rows));
        }

        public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options)
        {
            return 1;
        }

        public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId)
        {
            _closedChannelIds.Enqueue(channelId);
        }

        public int RequestRemoteForward(NovaSshSafeHandle sessionHandle, string bindAddress, int port)
        {
            if (RefuseRemoteForwards)
            {
                throw new InvalidOperationException($"The server refused the remote forward on {bindAddress}:{port}.");
            }

            _remoteForwardRequests.Enqueue((bindAddress, port));
            return port;
        }

        public void Close(NovaSshSafeHandle sessionHandle)
        {
            CloseCallCount++;
        }

        public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }

        public void Enqueue(NativeSshEvent nextEvent)
        {
            _events.Enqueue(nextEvent);
        }
    }
}
