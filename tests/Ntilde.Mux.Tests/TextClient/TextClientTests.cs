using System.Text.RegularExpressions;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

/// <summary>Phase 3 spec §6.4, §6.5: the text client end to end over an in-memory daemon and a fake console.</summary>
public sealed class TextClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<int> RunAsync(TextClientSession client) => Task.Run(() => client.Run(TextWriter.Null), Ct);

    [Fact]
    public async Task Attach_paints_the_session_and_the_chord_detaches_with_exit_0()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        host.Fake(id).Emit("hello from the shell\r\n");
        MuxClient attacher = await host.ConnectClientAsync(new MuxClientOptions { ClientKind = "ntilde-attach" });
        using var console = new FakeConsoleSurface(80, 24);
        using var client = new TextClientSession(attacher, id, console);

        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => console.ScreenText().Contains("hello from the shell", StringComparison.Ordinal), "the session was painted");
        Assert.True(console.IsRaw);
        console.Type("\u001c");
        console.Type("d");

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(console.IsRaw);
        Assert.StartsWith(TextClientRenderer.EnterSequence, console.Output, StringComparison.Ordinal);
        Assert.Contains(TextClientRenderer.LeaveSequence, console.Output, StringComparison.Ordinal);
        // Nothing between the leave sequence and the exit line: no frame lands on the restored screen.
        Assert.EndsWith(TextClientRenderer.LeaveSequence + $"[detached from {id}]\r\n", console.Output, StringComparison.Ordinal);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.False(host.Mux(id).IsExited);
    }

    /// <summary>Final review: a render thread that never started makes Join throw; the console is still restored.</summary>
    [Fact]
    public async Task A_render_thread_that_never_started_still_restores_the_console()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console)
        {
            BeforeRenderStartForTest = () => throw new InvalidOperationException("scripted: the render thread could not start"),
        };

        Assert.Equal(2, await RunAsync(client).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(TextClientExit.Error, client.ExitReason);
        Assert.False(console.IsRaw);
        Assert.Equal(1, console.RestoreCount);
    }

    [Fact]
    public async Task Typed_input_reaches_the_session_and_two_prefixes_send_one_literal()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        console.Type("ls\r");
        console.Type("\u001c\u001c");

        await TestWait.UntilAsync(() => string.Concat(host.Fake(id).SentInput) == "ls\r\u001c", "the input arrived, the chord prefix once");
        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Input_closed_counts_as_a_detach()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        console.Dispose(); // stdin reaches end of file

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(TextClientExit.Detached, client.ExitReason);
        Assert.False(console.IsRaw);
        Assert.EndsWith($"[detached from {id}]\r\n", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_exit_returns_1_and_prints_the_code()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        host.Fake(id).Exit(7);

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.EndsWith(TextClientRenderer.LeaveSequence + "[session exited with code 7]\r\n", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Killed_elsewhere_returns_1_and_says_so()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        await spawner.KillAsync(id, Ct);

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.EndsWith("[session ended from another window]\r\n", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_sends_no_input_requests_no_resize_and_says_so()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);            // 80x24
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(100, 30);
        using var client = new TextClientSession(attacher, id, console, new TextClientOptions { ReadOnly = true });
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => console.ScreenText().Contains("read-only", StringComparison.Ordinal), "the status line says read-only");

        console.Type("typed");
        console.Resize(120, 40);
        await TestWait.UntilAsync(() => console.ScreenText().Contains("read-only", StringComparison.Ordinal), "repainted after the resize");
        await attacher.PingAsync(Ct);
        await host.Mux(id).InvokeAsync(() => 0);
        await TestWait.UntilAsync(() => host.Mux(id).QueuedInputBytes == 0, "the input writer is idle");

        Assert.Empty(host.Fake(id).SentInput);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Read_only_on_a_v1_daemon_fails_with_2_and_sends_no_attach()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MaxProtocolVersion = 1, ForceConPtyFiltering = false });
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console, new TextClientOptions { ReadOnly = true });
        var stderr = new StringWriter();

        int code = await Task.Run(() => client.Run(stderr), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(2, code);
        Assert.Equal(TextClientExit.AttachFailed, client.ExitReason);
        Assert.StartsWith("mux: attach failed:", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(console.IsRaw);
        await attacher.PingAsync(Ct);
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.Equal(0, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task A_console_resize_requests_the_new_grid()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(80, 24);
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        console.Resize(100, 30);

        await TestWait.UntilAsync(() => (host.Mux(id).Cols, host.Mux(id).Rows) == (100, 30), "the session took the console's grid");
        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task A_change_that_lands_while_a_frame_is_being_written_gets_its_own_frame()
    {
        // The wake is set before the render reads the buffer: a Changed raised after that read, while
        // the frame is still going out, must leave the wake set. A flag cleared after the render (or
        // a reset of the event) would lose it, and "second" would never reach the screen.
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(80, 24);
        using var inWrite = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int blocked = 0;
        console.BeforeWrite = text =>
        {
            if (!text.Contains("first", StringComparison.Ordinal) || Interlocked.Exchange(ref blocked, 1) != 0) return;
            inWrite.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        host.Fake(id).Emit("first\r\n");
        await TestWait.UntilAsync(() => inWrite.IsSet, "the frame with 'first' is being written");
        host.Fake(id).Emit("second\r\n");
        await host.SettleAsync(id, attacher);   // "second" is in the client's buffer and Changed has fired
        release.Set();

        await TestWait.UntilAsync(() => console.ScreenText().Contains("second", StringComparison.Ordinal), "the change during the write was painted");
        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Theory]
    [InlineData("detach")]
    [InlineData("exit")]
    [InlineData("killed")]
    [InlineData("disconnect")]
    [InlineData("attach-failed")]
    [InlineData("write-throws")]
    [InlineData("write-throws-non-io")]
    [InlineData("enter-write-throws-non-io")]
    [InlineData("leave-write-throws-non-io")]
    [InlineData("read-throws-non-io")]
    [InlineData("signal")]
    public async Task Console_modes_are_restored_on_every_exit_path(string path)
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(80, 24);
        switch (path)
        {
            case "write-throws":
                console.ThrowOnWriteNumber = 2;   // #1 is the enter sequence, #2 the first frame
                break;
            case "write-throws-non-io":           // on the render thread
                console.ThrowOnWriteNumber = 2;
                console.WriteFailure = () => new ScriptedConsoleFault();
                break;
            case "enter-write-throws-non-io":     // on the thread in Run, after EnterRawMode
                console.ThrowOnWriteNumber = 1;
                console.WriteFailure = () => new ScriptedConsoleFault();
                break;
            case "leave-write-throws-non-io":     // in the cleanup, just before RestoreMode
                console.BeforeWrite = text =>
                {
                    if (text == TextClientRenderer.LeaveSequence) throw new ScriptedConsoleFault();
                };
                break;
            case "read-throws-non-io":            // on the input thread
                console.ReadFailure = () => new ScriptedConsoleFault();
                break;
        }

        Guid target = path == "attach-failed" ? Guid.NewGuid() : id;
        using var client = new TextClientSession(attacher, target, console);
        var stderr = new StringWriter();

        Task<int> run = Task.Run(() => client.Run(stderr), Ct);
        bool failsByItself = path is "attach-failed" or "write-throws" or "write-throws-non-io" or "enter-write-throws-non-io" or "read-throws-non-io";
        if (!failsByItself)
        {
            await TestWait.UntilAsync(() => console.IsRaw && host.Mux(id).AttachedClients == 1, "attached");
        }

        switch (path)
        {
            case "detach" or "leave-write-throws-non-io": console.Type("\u001cd"); break;
            case "exit": host.Fake(id).Exit(0); break;
            case "killed": await spawner.KillAsync(id, Ct); break;
            case "disconnect": attacher.Dispose(); break;
            case "signal": client.RequestStop(TextClientExit.Detached, "signal"); break;
        }

        int code = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(console.IsRaw, $"{path}: the console was left raw");
        Assert.Equal(1, console.EnterRawCount);
        Assert.Equal(console.EnterRawCount, console.RestoreCount);
        // (Except where the leave sequence's own write is the scripted failure.)
        if (path != "leave-write-throws-non-io" && console.Output.Contains("\x1b[?1049h", StringComparison.Ordinal))
        {
            Assert.Contains("\x1b[?1049l", console.Output, StringComparison.Ordinal);
        }

        Assert.Equal(path switch { "detach" or "signal" or "leave-write-throws-non-io" => 0, "exit" or "killed" => 1, _ => 2 }, code);
        switch (path)
        {
            case "attach-failed":
                Assert.Equal(TextClientExit.AttachFailed, client.ExitReason);
                Assert.StartsWith("mux: attach failed:", stderr.ToString(), StringComparison.Ordinal);
                break;
            case "write-throws":
                Assert.Equal(TextClientExit.Error, client.ExitReason);
                Assert.StartsWith("mux: scripted console failure", stderr.ToString(), StringComparison.Ordinal);
                break;
            case "write-throws-non-io" or "enter-write-throws-non-io" or "read-throws-non-io":
                Assert.Equal(TextClientExit.Error, client.ExitReason);
                Assert.StartsWith($"mux: {ScriptedConsoleFault.Text}", stderr.ToString(), StringComparison.Ordinal);
                break;
        }
    }

    [Theory]
    [InlineData("chord", true)]
    [InlineData("input-closed", false)]
    [InlineData("signal", false)]
    public async Task Only_the_chord_is_a_deliberate_detach(string how, bool expectedDetachedByUser)
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        Assert.True(attacher.ProtocolVersion >= MuxProtocol.SessionEventsVersion);
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        switch (how)
        {
            case "chord": console.Type("\u001cd"); break;
            case "input-closed": console.Dispose(); break;
            case "signal": client.RequestStop(TextClientExit.Detached, "signal"); break;
        }

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        SessionSummary summary = Assert.Single(await spawner.ListSessionsAsync(Ct), s => s.SessionId == id);
        Assert.Equal(expectedDetachedByUser, summary.DetachedByUser);
    }

    [Fact]
    public async Task Device_queries_in_the_stream_never_reach_the_console()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(80, 24);
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        // DA1, CPR and XTGETTCAP ("TN"), as a program would send them.
        host.Fake(id).Emit("before\r\n\x1b[c\x1b[6n\x1bP+q544e\x1b\\after\r\n");

        await TestWait.UntilAsync(() => console.ScreenText().Contains("after", StringComparison.Ordinal), "the output after the queries was painted");
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Count >= 2, "the mux answered DA and CPR itself");
        string written = console.Output;

        Assert.DoesNotContain("\x1b[c", written, StringComparison.Ordinal);            // DA request
        Assert.DoesNotContain("\x1b[6n", written, StringComparison.Ordinal);           // CPR request
        Assert.DoesNotContain("\x1bP", written, StringComparison.Ordinal);             // the XTGETTCAP request, and any DCS reply
        Assert.DoesNotMatch(new Regex("\x1b\\[\\?[0-9;]*c"), written);                 // a DA reply
        Assert.DoesNotMatch(new Regex("\x1b\\[[0-9]+;[0-9]+R"), written);              // a CPR reply
        Assert.All(host.Fake(id).SentInput, reply => Assert.DoesNotContain(reply, written, StringComparison.Ordinal));

        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    /// <summary>Not an IOException, nor anything else the client names: the catch-all paths must handle it.</summary>
    private sealed class ScriptedConsoleFault : Exception
    {
        public const string Text = "scripted non-IO console fault";

        public ScriptedConsoleFault() : base(Text) { }
    }
}
