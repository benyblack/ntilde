using System.Text;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Tests.Infra;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>
/// <see cref="NativeSshExecTransport"/> (Phase 4 spec §8.3) against a scripted native layer: how its one
/// poll thread routes each exec-mode event (task-14 ABI) into the channel's streams, tail and completion.
/// </summary>
public sealed class NativeSshExecTransportTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static SshProfile Profile() => new()
    {
        Id = Guid.Parse("8f6d7f63-0d2c-4c55-9c39-6f3f5f8b2a10"),
        Name = "Prod",
        BackendKind = SshBackendKind.Native,
        User = "alice",
        Host = "example.com",
        Port = 2200,
        ServerAliveIntervalSeconds = 15,
        JumpHops = [new SshJumpHop { Host = "bastion", User = "jump", Port = 2222 }],
    };

    private static NativeSshExecTransport Transport(
        ScriptedNativeSshInterop interop,
        ISshInteractionHandler? handler = null,
        SshProfile? profile = null) =>
        new(profile ?? Profile(), interop, handler, NativeSshConnectionOptionsFactory.Create, log: _ => { });

    private static ISshExecChannel Start(ScriptedNativeSshInterop interop, ISshInteractionHandler? handler = null) =>
        Transport(interop, handler).Start("ntilde-mux proxy --stdio", CancellationToken.None);

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private const int ChunkBytes = 1024 * 1024;

    /// <summary>Queues one-MiB stdout chunks, four past what fills the stdout queue; returns how many.</summary>
    private static int EnqueueChunksPastThePauseThreshold(ScriptedNativeSshInterop interop, out int chunksToFill)
    {
        chunksToFill = (int)(NativeSshExecChannel.StdoutPauseThresholdBytes / ChunkBytes);
        int chunks = chunksToFill + 4;
        for (int i = 0; i < chunks; i++)
        {
            interop.Enqueue(NativeSshEvent.Data(Enumerable.Repeat((byte)i, ChunkBytes).ToArray()));
        }

        return chunks;
    }

    /// <summary>One synchronous <see cref="Stream.Read(Span{byte})"/> on a dedicated thread, as the mux client reads.</summary>
    private static (Thread Reader, Task<string> Read) StartSynchronousRead(Stream stdout)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Thread(() =>
        {
            try
            {
                byte[] buffer = new byte[64];
                int read = stdout.Read(buffer);
                result.TrySetResult(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "TestSyncReader" };
        reader.Start();
        return (reader, result.Task);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // --- Start -----------------------------------------------------------------------------------

    [Fact]
    public void Start_execs_the_command_with_the_options_the_factory_builds_for_the_profile()
    {
        var interop = new ScriptedNativeSshInterop();
        SshProfile? seen = null;
        var transport = new NativeSshExecTransport(Profile(), interop, null, profile =>
        {
            seen = profile;
            return NativeSshConnectionOptionsFactory.Create(profile);
        }, log: _ => { });

        using ISshExecChannel channel = transport.Start("ntilde-mux proxy --stdio", CancellationToken.None);

        Assert.Equal("alice@example.com", transport.DisplayName);
        Assert.Equal("example.com", seen!.Host);
        Assert.Equal("ntilde-mux proxy --stdio", interop.ExecCommand);
        NativeSshConnectionOptions options = interop.ExecOptions!;
        Assert.Equal(("alice", "example.com", 2200), (options.User, options.Host, options.Port));
        Assert.Equal(15, options.KeepAliveIntervalSeconds);
        SshJumpHop hop = Assert.Single(options.JumpHops);
        Assert.Equal(("bastion", "jump", 2222), (hop.Host, hop.User, hop.Port));
    }

    [Fact]
    public void Start_cancelled_before_it_begins_throws_without_connecting()
    {
        var interop = new ScriptedNativeSshInterop();

        Assert.Throws<OperationCanceledException>(() =>
            Transport(interop).Start("true", new CancellationToken(canceled: true)));
        Assert.Equal(0, interop.ExecCalls);
    }

    [Fact]
    public void Start_surfaces_a_rejected_exec_as_its_exception()
    {
        var interop = new ScriptedNativeSshInterop { ExecFailure = new InvalidOperationException("Failed to create native SSH session.") };

        var ex = Assert.Throws<InvalidOperationException>(() => Start(interop));
        Assert.Equal("Failed to create native SSH session.", ex.Message);
    }

    // --- Events ----------------------------------------------------------------------------------

    [Fact]
    public async Task Stdout_bytes_arrive_in_order()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(
            ScriptedNativeSshInterop.Connected(),
            ScriptedNativeSshInterop.Stdout("NTILDE-MUX-PROXY 1 42\n"),
            ScriptedNativeSshInterop.Stdout("frame-1"),
            ScriptedNativeSshInterop.Stderr("noise"),
            ScriptedNativeSshInterop.Stdout("frame-2"),
            NativeSshEvent.ExitStatus(0),
            ScriptedNativeSshInterop.Closed());

        using ISshExecChannel channel = Start(interop);

        Assert.Equal("NTILDE-MUX-PROXY 1 42\nframe-1frame-2", await ReadToEndAsync(channel.Stdout));
    }

    [Fact]
    public async Task Stderr_goes_to_the_tail_and_is_complete_when_Completion_resolves()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(
            ScriptedNativeSshInterop.Stderr("mux: first\n"),
            ScriptedNativeSshInterop.Stdout("out"),
            ScriptedNativeSshInterop.Stderr("mux: second\n"),
            NativeSshEvent.ExitStatus(1),
            ScriptedNativeSshInterop.Closed());

        using ISshExecChannel channel = Start(interop);
        await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal("mux: first\nmux: second\n", channel.StderrTail);
    }

    [Fact]
    public async Task ExitStatus_then_Closed_completes_with_the_code()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(ScriptedNativeSshInterop.Connected(), NativeSshEvent.ExitStatus(3), ScriptedNativeSshInterop.Closed());

        using ISshExecChannel channel = Start(interop);

        Assert.Equal(3, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, await ReadToEndAsync(channel.Stdout));
    }

    [Fact]
    public async Task Closed_without_an_exit_status_completes_with_null()
    {
        // A command killed by a signal, or a lost transport: the native layer reports no status.
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(ScriptedNativeSshInterop.Connected(), ScriptedNativeSshInterop.Stdout("partial"), ScriptedNativeSshInterop.Closed());

        using ISshExecChannel channel = Start(interop);

        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Equal("partial", await ReadToEndAsync(channel.Stdout));
    }

    [Fact]
    public async Task An_error_before_Closed_fails_the_stdout_read_after_the_data_and_is_kept_in_the_tail()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(
            ScriptedNativeSshInterop.Connected(),
            ScriptedNativeSshInterop.Stdout("motd"),
            ScriptedNativeSshInterop.Error("the server refused to run the command"),
            ScriptedNativeSshInterop.Closed());

        using ISshExecChannel channel = Start(interop);

        byte[] buffer = new byte[64];
        int read = await channel.Stdout.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Equal("motd", Encoding.UTF8.GetString(buffer, 0, read));

        IOException failure = await Assert.ThrowsAnyAsync<IOException>(() =>
            channel.Stdout.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken));
        SshExecTransportException transportFailure = Assert.IsType<SshExecTransportException>(failure);
        Assert.Contains("the server refused to run the command", failure.Message, StringComparison.Ordinal);
        Assert.Equal("the server refused to run the command", transportFailure.NativeMessage);

        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Contains("the server refused to run the command", channel.StderrTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_key_prompt_reaches_the_handler_and_its_answer_goes_to_SubmitResponse()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(
            ScriptedNativeSshInterop.HostKeyPrompt(),
            ScriptedNativeSshInterop.Stdout("after auth"),
            NativeSshEvent.ExitStatus(0),
            ScriptedNativeSshInterop.Closed());
        var handler = new PendingInteractionHandler();

        using ISshExecChannel channel = Start(interop, handler);

        SshInteractionRequest request = await handler.Request.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Equal(SshInteractionKind.UnknownHostKey, request.Kind);
        Assert.Equal(("example.com", 2200, "SHA256:test"), (request.Host, request.Port, request.Fingerprint));
        Assert.Equal(Profile().Id, request.ProfileId);

        // Nothing past the prompt is read until it is answered: auth comes before the command's output.
        Assert.Equal(1, interop.Dequeued);
        Assert.Empty(interop.Submissions);

        handler.Complete(SshInteractionResponse.AcceptHostKey());

        Assert.Equal(0, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        (NativeSshResponseKind kind, string payload) = Assert.Single(interop.Submissions);
        Assert.Equal(NativeSshResponseKind.HostKeyDecision, kind);
        Assert.Equal("""{"accept":true}""", payload);
        Assert.Equal("after auth", await ReadToEndAsync(channel.Stdout));
    }

    [Fact]
    public async Task Password_prompts_carry_the_profile_and_offer_the_vault_once_per_connection()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(
            ScriptedNativeSshInterop.PasswordPrompt(),
            ScriptedNativeSshInterop.PasswordPrompt(),
            ScriptedNativeSshInterop.Closed());
        var handler = new RecordingInteractionHandler(SshInteractionResponse.FromSecret("hunter2"));

        using ISshExecChannel channel = Start(interop, handler);
        await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        SshInteractionRequest first = handler.Requests[0];
        Assert.Equal((Profile().Id, "Prod", "alice", "example.com"), (first.ProfileId!.Value, first.ProfileName, first.ProfileUser, first.ProfileHost));
        Assert.True(first.AllowVaultPasswordReuse);
        Assert.True(first.RememberPasswordInVault);
        Assert.False(handler.Requests[1].AllowVaultPasswordReuse);

        // Not an ActiveSshSessionRegistry session (spec §8.4): no session id to key a runtime password by.
        Assert.Null(first.SessionId);
        Assert.All(interop.Submissions, submission =>
        {
            Assert.Equal(NativeSshResponseKind.Password, submission.Kind);
            Assert.Equal("""{"text":"hunter2"}""", submission.PayloadJson);
        });
    }

    /// <summary>
    /// A handler with no answer to give (an automatic reconnect with no remembered password) aborts by
    /// throwing. The prompt then gets no response at all - a cancel would be submitted as an empty
    /// password, a failed login on the server - and the session is closed instead: rusty_ssh's pending
    /// wait_for_response returns None on the close, so its auth stops before sending anything.
    /// </summary>
    [Fact]
    public async Task A_handler_that_throws_aborts_the_session_without_answering_the_prompt()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(ScriptedNativeSshInterop.PasswordPrompt(), ScriptedNativeSshInterop.Stdout("never read"));
        var handler = new ThrowingInteractionHandler(new InvalidOperationException("nobody to ask"));

        using ISshExecChannel channel = Start(interop, handler);

        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Empty(interop.Submissions);
        Assert.Equal(1, interop.CloseCount);
        var failure = await Assert.ThrowsAsync<SshExecTransportException>(() => ReadToEndAsync(channel.Stdout));
        Assert.Contains("nobody to ask", failure.NativeMessage, StringComparison.Ordinal);
    }

    private sealed class ThrowingInteractionHandler(Exception error) : ISshInteractionHandler
    {
        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken) =>
            Task.FromException<SshInteractionResponse>(error);
    }

    // --- Stdin -----------------------------------------------------------------------------------

    [Fact]
    public async Task Stdin_writes_go_to_the_native_write_in_order_and_fail_once_the_command_has_ended()
    {
        var interop = new ScriptedNativeSshInterop();
        using ISshExecChannel channel = Start(interop);

        channel.Stdin.Write("ab"u8);
        await channel.Stdin.WriteAsync("cd"u8.ToArray(), TestContext.Current.CancellationToken);
        channel.Stdin.Flush();

        Assert.Equal(["ab", "cd"], interop.Writes.Select(w => Encoding.UTF8.GetString(w)));

        interop.Enqueue(NativeSshEvent.ExitStatus(0), ScriptedNativeSshInterop.Closed());
        await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Throws<IOException>(() => channel.Stdin.Write("x"u8));
        Assert.Equal(2, interop.Writes.Count);
    }

    [Fact]
    public async Task Disposing_stdin_sends_EOF_once()
    {
        var interop = new ScriptedNativeSshInterop
        {
            // `cat`: ends when its stdin does.
            OnSendEof = fake => fake.Enqueue(NativeSshEvent.ExitStatus(0), ScriptedNativeSshInterop.Closed()),
        };
        ISshExecChannel channel = Start(interop);

        channel.Stdin.Dispose();
        channel.Stdin.Dispose();
        Assert.Equal(0, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        channel.Dispose();

        Assert.Equal(1, interop.SendEofCount);
        Assert.Throws<ObjectDisposedException>(() => channel.Stdin.Write("x"u8));
    }

    // --- Dispose ---------------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_the_channel_sends_EOF_and_keeps_the_exit_code_of_a_command_that_ends_on_it()
    {
        var interop = new ScriptedNativeSshInterop
        {
            OnSendEof = fake => fake.Enqueue(NativeSshEvent.ExitStatus(0), ScriptedNativeSshInterop.Closed()),
        };
        ISshExecChannel channel = Start(interop);
        await WaitUntilAsync(() => interop.Polls > 0, "the first poll");

        channel.Dispose();

        Assert.Equal(1, interop.SendEofCount);
        Assert.Equal(1, interop.CloseCount);
        Assert.True(interop.Handle.IsClosed);
        Assert.False(interop.PollThread!.IsAlive);
        Assert.Equal("SshExecPoll", interop.PollThread.Name);
        Assert.Equal(0, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_the_channel_of_a_hung_command_closes_the_handle_ends_the_thread_and_releases_a_blocked_reader()
    {
        var interop = new ScriptedNativeSshInterop(); // never exits, not even on EOF
        ISshExecChannel channel = Start(interop);
        await WaitUntilAsync(() => interop.Polls > 0, "the first poll");
        Task<int> blockedRead = channel.Stdout.ReadAsync(new byte[16], TestContext.Current.CancellationToken).AsTask();

        Task dispose = Task.Run(channel.Dispose, TestContext.Current.CancellationToken);
        await dispose.WaitAsync(NativeSshExecChannel.ExitGrace + Bound, TestContext.Current.CancellationToken);

        Assert.Equal(1, interop.SendEofCount);
        Assert.Equal(1, interop.CloseCount);
        Assert.True(interop.Handle.IsClosed);
        Assert.False(interop.PollThread!.IsAlive);
        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Equal(0, await blockedRead.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_the_channel_while_the_poll_thread_waits_on_a_full_queue_frees_it_and_keeps_the_exit_code()
    {
        var interop = new ScriptedNativeSshInterop();
        int chunks = EnqueueChunksPastThePauseThreshold(interop, out int chunksToFill);
        interop.Enqueue(NativeSshEvent.ExitStatus(0), ScriptedNativeSshInterop.Closed());
        ISshExecChannel channel = Start(interop);
        await WaitUntilAsync(() => interop.Dequeued >= chunksToFill, "the queue to fill");

        await Task.Run(channel.Dispose, TestContext.Current.CancellationToken)
            .WaitAsync(NativeSshExecChannel.ExitGrace + NativeSshExecChannel.StopWait + Bound, TestContext.Current.CancellationToken);

        // Abandoning stdout woke the parked poll thread, which dropped the rest and reached Closed
        // within the grace period. Had it stayed parked, the channel would have stopped it: null.
        Assert.Equal(0, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Equal(chunks + 2, interop.Dequeued);
        Assert.Equal(1, interop.CloseCount);
        Assert.False(interop.PollThread!.IsAlive);
    }

    [Fact]
    public async Task Disposing_the_channel_during_a_pending_prompt_cancels_the_handler_and_records_no_failure()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(ScriptedNativeSshInterop.HostKeyPrompt());
        var handler = new PendingInteractionHandler();
        ISshExecChannel channel = Start(interop, handler);
        await handler.Request.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await Task.Run(channel.Dispose, TestContext.Current.CancellationToken)
            .WaitAsync(NativeSshExecChannel.ExitGrace + NativeSshExecChannel.StopWait + Bound, TestContext.Current.CancellationToken);

        Assert.True(handler.Token.IsCancellationRequested);
        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Empty(interop.Submissions);
        Assert.DoesNotContain("native ssh", channel.StderrTail, StringComparison.Ordinal);
        Assert.Equal(0, channel.Stdout.Read(new byte[16])); // the end, and no transport failure to throw
        Assert.Equal(1, interop.CloseCount);
        Assert.False(interop.PollThread!.IsAlive);
    }

    // --- Synchronous reads (the mux client's read thread) ----------------------------------------

    [Fact]
    public async Task A_synchronous_read_waiting_on_an_empty_queue_is_woken_by_the_poll_threads_data()
    {
        var interop = new ScriptedNativeSshInterop();
        using ISshExecChannel channel = Start(interop);
        (Thread reader, Task<string> read) = StartSynchronousRead(channel.Stdout);
        await WaitUntilAsync(() => reader.ThreadState.HasFlag(ThreadState.WaitSleepJoin), "the reader to wait");

        interop.Enqueue(ScriptedNativeSshInterop.Stdout("frame"));

        Assert.Equal("frame", await read.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_stdout_releases_a_waiting_synchronous_read_and_the_command_runs_on()
    {
        var interop = new ScriptedNativeSshInterop();
        using ISshExecChannel channel = Start(interop);
        (Thread reader, Task<string> read) = StartSynchronousRead(channel.Stdout);
        await WaitUntilAsync(() => reader.ThreadState.HasFlag(ThreadState.WaitSleepJoin), "the reader to wait");

        channel.Stdout.Dispose();

        Assert.Equal(string.Empty, await read.WaitAsync(Bound, TestContext.Current.CancellationToken));

        // Nobody reads stdout any more: the poll thread drops it and carries on to the exit.
        interop.Enqueue(ScriptedNativeSshInterop.Stdout(new string('x', 1024)), NativeSshEvent.ExitStatus(5), ScriptedNativeSshInterop.Closed());
        Assert.Equal(5, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => channel.Stdout.Read(new byte[16]));
    }

    [Fact]
    public async Task A_failing_poll_ends_the_channel_with_a_transport_failure()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(ScriptedNativeSshInterop.Stdout("before"));
        using ISshExecChannel channel = Start(interop);
        byte[] buffer = new byte[64];
        Assert.Equal(6, await channel.Stdout.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken));

        interop.PollFailure = new InvalidOperationException("Native SSH poll failed with result -7.");

        SshExecTransportException failure = await Assert.ThrowsAsync<SshExecTransportException>(() =>
            channel.Stdout.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Contains("Native SSH poll failed with result -7.", failure.NativeMessage, StringComparison.Ordinal);
        Assert.Null(await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.Contains("Native SSH poll failed with result -7.", channel.StderrTail, StringComparison.Ordinal);
        Assert.Equal(1, interop.CloseCount);
    }

    // --- SshExec over the native transport -------------------------------------------------------

    [Fact]
    public async Task SshExec_returns_a_native_connection_failure_as_a_result_as_it_does_OpenSSHs_255()
    {
        var interop = new ScriptedNativeSshInterop();
        interop.Enqueue(
            ScriptedNativeSshInterop.Connected(),
            ScriptedNativeSshInterop.Stdout("motd\n"),
            ScriptedNativeSshInterop.Error("authentication failed"),
            ScriptedNativeSshInterop.Closed());

        SshExecResult result = await SshExec.RunAsync(Transport(interop), "uname -sm", ReadOnlyMemory<byte>.Empty, null, Bound, CancellationToken.None)
            .WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Null(result.ExitCode);
        Assert.Equal("motd\n", result.Stdout);
        Assert.Contains("authentication failed", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, interop.CloseCount);
    }

    // --- Backpressure ----------------------------------------------------------------------------

    [Fact]
    public async Task A_consumer_that_stops_reading_stops_the_polling_and_reading_resumes_it()
    {
        var interop = new ScriptedNativeSshInterop();
        int chunks = EnqueueChunksPastThePauseThreshold(interop, out int chunksToFill);
        interop.Enqueue(NativeSshEvent.ExitStatus(0), ScriptedNativeSshInterop.Closed());

        using ISshExecChannel channel = Start(interop);
        await WaitUntilAsync(() => interop.Dequeued >= chunksToFill, "the pipe to fill");

        // Parked on the full pipe: nothing more leaves the native queue, which is what pushes back on
        // the remote (rusty_ssh's 4 MiB budget, then the SSH window, then TCP). A wait can only miss a
        // regression here, never fail a correct transport.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.InRange(interop.Dequeued, chunksToFill, chunksToFill + 1);
        Assert.False(channel.Completion.IsCompleted);

        using var drained = new MemoryStream();
        await channel.Stdout.CopyToAsync(drained, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal((long)chunks * ChunkBytes, drained.Length);
        byte[] bytes = drained.ToArray();
        Assert.All(Enumerable.Range(0, chunks), i => Assert.Equal((byte)i, bytes[(long)i * ChunkBytes]));
        Assert.Equal(0, await channel.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    // --- Interop ---------------------------------------------------------------------------------

    [Fact]
    public void The_real_interop_rejects_a_blank_command_before_reaching_the_native_layer()
    {
        var options = NativeSshConnectionOptionsFactory.Create(Profile());

        Assert.ThrowsAny<ArgumentException>(() => new NativeSshInterop().Exec(options, "  "));
    }

    // --- End to end ------------------------------------------------------------------------------

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task Native_exec_runs_a_command_and_reports_its_exit_code()
    {
        await using var fixture = await DockerSshFixture.StartAsync();
        var profile = new SshProfile
        {
            Id = Guid.Parse("c3d6f0a2-62f4-4bb8-9d1c-0c1f4a9e7b55"),
            Name = "Docker Native Exec",
            BackendKind = SshBackendKind.Native,
            Host = fixture.Host,
            User = fixture.UserName,
            Port = fixture.Port,
        };
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var transport = new NativeSshExecTransport(profile, new NativeSshInterop(), handler, NativeSshConnectionOptionsFactory.Create, log: _ => { });

        using ISshExecChannel channel = transport.Start("sh -c 'echo out; echo err 1>&2; exit 3'", CancellationToken.None);
        channel.Stdin.Dispose();

        using var stdout = new MemoryStream();
        await channel.Stdout.CopyToAsync(stdout, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("out\n", Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Equal(3, await channel.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Contains("err", channel.StderrTail, StringComparison.Ordinal);
    }

    private sealed class PendingInteractionHandler : ISshInteractionHandler
    {
        private readonly TaskCompletionSource<SshInteractionRequest> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SshInteractionResponse> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SshInteractionRequest> Request => _request.Task;

        /// <summary>The token the handler was given: the channel cancels it when it stops.</summary>
        public CancellationToken Token { get; private set; }

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            _request.TrySetResult(request);
            return _response.Task.WaitAsync(cancellationToken);
        }

        public void Complete(SshInteractionResponse response) => _response.TrySetResult(response);
    }

    private sealed class RecordingInteractionHandler(SshInteractionResponse answer) : ISshInteractionHandler
    {
        private readonly List<SshInteractionRequest> _requests = [];

        public IReadOnlyList<SshInteractionRequest> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(answer);
        }
    }
}
