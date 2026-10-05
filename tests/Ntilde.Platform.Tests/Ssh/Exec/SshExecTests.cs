using System.Text;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary><see cref="SshExec.RunAsync"/>: one-shot remote commands (the probe and the upload, Phase 4 spec §9).</summary>
public sealed class SshExecTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Runs_the_command_feeds_stdin_in_chunks_and_returns_the_exit_code_and_output()
    {
        byte[] payload = new byte[200 * 1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + (i % 26));
        var transport = new FakeExecTransport(() => FakeExecChannel.Echo(exitCode: 7));
        var progress = new RecordingProgress();

        SshExecResult result = await SshExec.RunAsync(transport, "cat", payload, progress, Generous, CancellationToken.None)
            .WaitAsync(Generous, TestContext.Current.CancellationToken);

        Assert.Equal("cat", transport.LastCommand);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(Encoding.ASCII.GetString(payload), result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(new long[] { 65536, 131072, 196608, 204800 }, progress.Reports);
        FakeExecChannel channel = transport.Channel!;
        Assert.True(channel.StdinClosed, "stdin must get EOF once the payload is written");
        Assert.Equal(payload.Length, channel.StdinBytes);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task Empty_stdin_is_an_immediate_eof_with_no_progress()
    {
        var transport = new FakeExecTransport(() => FakeExecChannel.Printing("Linux x86_64\n"u8.ToArray(), exitCode: 0, stderr: "warning: motd"));
        var progress = new RecordingProgress();

        SshExecResult result = await SshExec.RunAsync(transport, "uname -sm", ReadOnlyMemory<byte>.Empty, progress, Generous, CancellationToken.None)
            .WaitAsync(Generous, TestContext.Current.CancellationToken);

        Assert.Equal(new SshExecResult(0, "Linux x86_64\n", "warning: motd"), result);
        Assert.Empty(progress.Reports);
        Assert.True(transport.Channel!.StdinClosed);
    }

    [Fact]
    public async Task Stdout_is_capped_at_one_mebibyte_and_the_rest_is_drained()
    {
        byte[] flood = new byte[(1024 * 1024) + 4096];
        Array.Fill(flood, (byte)'z');
        var transport = new FakeExecTransport(() => FakeExecChannel.Printing(flood, exitCode: 0));

        SshExecResult result = await SshExec.RunAsync(transport, "yes z", ReadOnlyMemory<byte>.Empty, null, Generous, CancellationToken.None)
            .WaitAsync(Generous, TestContext.Current.CancellationToken);

        Assert.Equal(1024 * 1024, result.Stdout.Length);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_remote_that_stops_reading_stdin_still_reports_its_exit_status()
    {
        var transport = new FakeExecTransport(() => FakeExecChannel.RefusingStdin(exitCode: 127, stderr: "sh: 1: cat: not found"));

        SshExecResult result = await SshExec.RunAsync(transport, "cat > x", new byte[100_000], null, Generous, CancellationToken.None)
            .WaitAsync(Generous, TestContext.Current.CancellationToken);

        Assert.Equal(127, result.ExitCode);
        Assert.Equal("sh: 1: cat: not found", result.Stderr);
        Assert.True(transport.Channel!.StdinClosed);
    }

    [Fact]
    public async Task Timeout_disposes_the_channel_and_throws_TimeoutException()
    {
        var transport = new FakeExecTransport(FakeExecChannel.Hung);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            SshExec.RunAsync(transport, "sleep 999", ReadOnlyMemory<byte>.Empty, null, TimeSpan.FromMilliseconds(200), CancellationToken.None)
                .WaitAsync(Generous, TestContext.Current.CancellationToken));

        Assert.True(transport.Channel!.Disposed);
    }

    [Fact]
    public async Task Cancellation_disposes_the_channel_and_throws_OperationCanceledException()
    {
        var transport = new FakeExecTransport(FakeExecChannel.Hung);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SshExec.RunAsync(transport, "sleep 999", ReadOnlyMemory<byte>.Empty, null, Generous, cts.Token)
                .WaitAsync(Generous, TestContext.Current.CancellationToken));

        Assert.True(transport.Channel!.Disposed);
    }

    [Fact]
    public async Task A_cancelled_token_never_starts_the_command()
    {
        var transport = new FakeExecTransport(FakeExecChannel.Hung);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SshExec.RunAsync(transport, "true", ReadOnlyMemory<byte>.Empty, null, Generous, new CancellationToken(canceled: true)));

        Assert.Null(transport.Channel);
    }

    [Fact]
    public async Task A_start_failure_propagates()
    {
        var transport = new FakeExecTransport(() => throw new InvalidOperationException("ssh is not installed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SshExec.RunAsync(transport, "true", ReadOnlyMemory<byte>.Empty, null, Generous, CancellationToken.None));

        Assert.Equal("ssh is not installed", ex.Message);
    }

    [Fact]
    public async Task RunAsync_rejects_missing_inputs_and_a_non_positive_timeout()
    {
        var transport = new FakeExecTransport(FakeExecChannel.Hung);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SshExec.RunAsync(null!, "true", ReadOnlyMemory<byte>.Empty, null, Generous, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SshExec.RunAsync(transport, null!, ReadOnlyMemory<byte>.Empty, null, Generous, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SshExec.RunAsync(transport, "true", ReadOnlyMemory<byte>.Empty, null, TimeSpan.Zero, CancellationToken.None));
        Assert.Null(transport.Channel);
    }
}
