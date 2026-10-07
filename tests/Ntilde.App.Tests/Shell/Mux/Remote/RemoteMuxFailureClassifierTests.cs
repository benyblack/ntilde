using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// Why a remote connect failed, in words the user can act on (Phase 4 spec §7.1): from the exit status,
/// what the remote side printed, and the exception the handshake ended with. The samples are what the
/// real shells, loaders and ssh print.
/// </summary>
public sealed class RemoteMuxFailureClassifierTests
{
    private const string Binary = "/home/nova/.local/share/ntilde/bin/ntilde-mux";

    private const string GlibcStraightQuote =
        "/home/nova/.local/share/ntilde/bin/ntilde-mux: /lib/x86_64-linux-gnu/libc.so.6: version 'GLIBC_2.38' not found (required by /home/nova/.local/share/ntilde/bin/ntilde-mux)";

    private const string GlibcBacktick =
        "/home/nova/.local/share/ntilde/bin/ntilde-mux: /lib/x86_64-linux-gnu/libc.so.6: version `GLIBC_2.38' not found (required by /home/nova/.local/share/ntilde/bin/ntilde-mux)";

    private const string Musl =
        "Error loading shared library ld-linux-x86-64.so.2: No such file or directory (needed by /home/nova/.local/share/ntilde/bin/ntilde-mux)";

    [Theory]
    // dash, running the default command's exec of a binary that is not there
    [InlineData(127, "sh: 1: " + Binary + ": not found\n", nameof(RemoteFailureKind.NotInstalled), "ntilde-mux is not installed")]
    [InlineData(127, "sh: 1: exec: " + Binary + ": not found\n", nameof(RemoteFailureKind.NotInstalled), "ntilde-mux is not installed")]
    // bash
    [InlineData(127, "bash: line 1: " + Binary + ": No such file or directory\n", nameof(RemoteFailureKind.NotInstalled), "ntilde-mux is not installed")]
    // the text alone decides when the exit status is unknown (the native backend after a signal)
    [InlineData(null, "sh: 1: " + Binary + ": not found\n", nameof(RemoteFailureKind.NotInstalled), "ntilde-mux is not installed")]
    // glibc older than the binary needs: the loader exits 127, and its line has "not found" next to the
    // binary's path - so the loader's own wording is checked before the not-installed rule
    [InlineData(127, GlibcStraightQuote + "\n", nameof(RemoteFailureKind.Unsupported), GlibcStraightQuote)]
    [InlineData(127, GlibcBacktick + "\n", nameof(RemoteFailureKind.Unsupported), GlibcBacktick)]
    // musl (gcompat's loader): no glibc to load at all
    [InlineData(127, Musl + "\n", nameof(RemoteFailureKind.Unsupported), Musl)]
    // the wrong architecture
    [InlineData(126, "sh: 1: " + Binary + ": Exec format error\n", nameof(RemoteFailureKind.Unsupported), "sh: 1: " + Binary + ": Exec format error")]
    [InlineData(126, "bash: line 1: " + Binary + ": cannot execute binary file: Exec format error\n", nameof(RemoteFailureKind.Unsupported),
        "bash: line 1: " + Binary + ": cannot execute binary file: Exec format error")]
    [InlineData(126, "sh: 1: " + Binary + ": Permission denied\n", nameof(RemoteFailureKind.Unsupported), "sh: 1: " + Binary + ": Permission denied")]
    // OpenSSH's own failure: exit 255, the reason on its last stderr line
    [InlineData(255, "ssh: connect to host x port 22: Connection refused\r\n", nameof(RemoteFailureKind.SshFailed), "ssh: connect to host x port 22: Connection refused")]
    [InlineData(255, "Warning: Permanently added 'x' (ED25519) to the list of known hosts.\r\nnova@x: Permission denied (publickey,password).\r\n\r\n",
        nameof(RemoteFailureKind.SshFailed), "nova@x: Permission denied (publickey,password).")]
    public void Classifies_what_real_shells_loaders_and_ssh_print(int? exitCode, string stderr, string kind, string reason)
    {
        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(exitCode, string.Empty, stderr, Handshake("ended"), "nova@x");

        Assert.Equal(Enum.Parse<RemoteFailureKind>(kind), failure.Kind);
        Assert.Equal(reason, failure.Reason);
    }

    /// <summary>
    /// Ruling 1 of Task 20: an automatic attempt runs OpenSSH in batch mode, which tries only what needs no
    /// answer. Refused, it needs the user (a password, a key's passphrase): the reconnect loop stops there
    /// rather than knock again every 30 s. A user's attempt refused the same way is an SSH failure.
    /// </summary>
    [Theory]
    [InlineData(true, 255, "nova@x: Permission denied (publickey,password).\r\n", nameof(RemoteFailureKind.NeedsUser))]
    [InlineData(true, 255, "Warning: Permanently added 'x' (ED25519) to the list of known hosts.\r\nnova@x: Permission denied (publickey).\r\n", nameof(RemoteFailureKind.NeedsUser))]
    [InlineData(false, 255, "nova@x: Permission denied (publickey,password).\r\n", nameof(RemoteFailureKind.SshFailed))]
    [InlineData(true, 255, "ssh: connect to host x port 22: Connection refused\r\n", nameof(RemoteFailureKind.SshFailed))]
    [InlineData(true, 126, "sh: 1: " + Binary + ": Permission denied\n", nameof(RemoteFailureKind.Unsupported))]
    // sshd past MaxAuthTries cuts the connection instead of refusing the last try: a refusal all the same, which another
    // automatic attempt would only repeat - with a saved password, one more failed login each time
    [InlineData(true, 255, "Received disconnect from 10.0.0.2 port 22:2: Too many authentication failures\r\nDisconnected from 10.0.0.2 port 22\r\n", nameof(RemoteFailureKind.NeedsUser))]
    [InlineData(false, 255, "Received disconnect from 10.0.0.2 port 22:2: Too many authentication failures\r\nDisconnected from 10.0.0.2 port 22\r\n", nameof(RemoteFailureKind.SshFailed))]
    public void An_automatic_attempt_that_ssh_refused_needs_the_user(bool automatic, int exitCode, string stderr, string kind)
    {
        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(exitCode, string.Empty, stderr, Handshake("ended"), "nova@x", automatic);

        Assert.Equal(Enum.Parse<RemoteFailureKind>(kind), failure.Kind);
        Assert.Equal(RemoteMuxFailureClassifier.Classify(exitCode, string.Empty, stderr, Handshake("ended"), "nova@x").Reason, failure.Reason);
    }

    [Fact]
    public void Version_mismatch_names_the_daemons_protocols_and_the_apps()
    {
        var error = new MuxProtocolException(MuxErrorCodes.VersionMismatch, "Server speaks 3..4; client offered 1..2.");

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(3, string.Empty, string.Empty, error, "nova@box");

        Assert.Equal(RemoteFailureKind.VersionMismatch, failure.Kind);
        Assert.Equal(
            $"ntilde-mux on nova@box speaks protocol 3-4; this app speaks {MuxProtocol.MinSupportedVersion}-{MuxProtocol.MaxSupportedVersion}",
            failure.Reason);
    }

    [Fact]
    public void Version_mismatch_found_by_the_client_names_the_version_the_daemon_chose()
    {
        var error = new MuxProtocolException(MuxErrorCodes.VersionMismatch, "Server chose protocol 5, outside this client's 1..2.");

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(null, string.Empty, string.Empty, error, "box");

        Assert.Equal(RemoteFailureKind.VersionMismatch, failure.Kind);
        Assert.StartsWith("ntilde-mux on box speaks protocol 5; this app speaks ", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_mismatch_wins_over_whatever_else_was_printed()
    {
        var error = new MuxProtocolException(MuxErrorCodes.VersionMismatch, "something unexpected");

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(127, string.Empty, "sh: 1: " + Binary + ": not found\n", error);

        Assert.Equal(RemoteFailureKind.VersionMismatch, failure.Kind);
        Assert.StartsWith("ntilde-mux on the remote host speaks another protocol version; this app speaks ", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_protocol_error_is_not_a_version_mismatch()
    {
        var error = new MuxProtocolException(MuxErrorCodes.ProtocolError, "bad frame");

        Assert.Equal(RemoteFailureKind.ProxyFailed, RemoteMuxFailureClassifier.Classify(null, string.Empty, string.Empty, error).Kind);
    }

    [Fact]
    public void A_native_transport_failure_is_SshFailed_with_the_native_message()
    {
        var native = new SshExecTransportException("SSH to nova@x failed: Authentication failed", "Authentication failed");

        RemoteMuxFailure direct = RemoteMuxFailureClassifier.Classify(null, string.Empty, "native ssh: Authentication failed\n", native);
        // How it usually arrives: the handshake's read of stdout met it.
        RemoteMuxFailure wrapped = RemoteMuxFailureClassifier.Classify(
            null, string.Empty, "native ssh: Authentication failed\n", new MuxProxyHandshakeException("Reading the remote output failed (x)", string.Empty, native));

        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.SshFailed, "Authentication failed"), direct);
        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.SshFailed, "Authentication failed"), wrapped);
    }

    [Fact]
    public void A_native_failure_is_SshFailed_even_when_its_message_mentions_a_missing_file()
    {
        // The native stderr tail carries the native message; it is not the remote command's output.
        const string message = "Failed to read identity file /home/me/.ssh/ntilde-mux_key: No such file or directory";
        var native = new SshExecTransportException("SSH to x failed: " + message, message);

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(null, string.Empty, "native ssh: " + message + "\n", native);

        Assert.Equal(RemoteFailureKind.SshFailed, failure.Kind);
        Assert.Equal(message, failure.Reason);
    }

    [Fact]
    public void Exit_255_with_nothing_on_stderr_still_says_ssh_failed()
    {
        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(255, string.Empty, " \r\n", Handshake("ended"));

        Assert.Equal(RemoteFailureKind.SshFailed, failure.Kind);
        Assert.Equal("ssh exited with code 255", failure.Reason);
    }

    [Fact]
    public void Anything_else_is_ProxyFailed_with_the_handshake_message()
    {
        MuxProxyHandshakeException error = Handshake("No ntilde-mux proxy greeting within 120 s", "Welcome to Ubuntu\\x0A");

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(null, error.CapturedText, string.Empty, error);

        Assert.Equal(new RemoteMuxFailure(RemoteFailureKind.ProxyFailed, error.Message), failure);
    }

    [Fact]
    public void ProxyFailed_adds_the_proxys_own_last_stderr_line()
    {
        // The proxy reports a daemon it could not reach on stderr, with exit 1 (spec §8.1).
        MuxProxyHandshakeException error = Handshake("The remote command ended without the ntilde-mux proxy's greeting");

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(1, string.Empty, "mux: The multiplexer did not come up within 10 s.\n", error);

        Assert.Equal(RemoteFailureKind.ProxyFailed, failure.Kind);
        Assert.Equal(error.Message + " (mux: The multiplexer did not come up within 10 s.)", failure.Reason);
    }

    [Fact]
    public void Text_printed_on_stdout_before_the_greeting_is_classified_too()
    {
        // StdioMuxTransport escapes control characters, so the captured lines end in \x0A.
        string captured = "Last login: today\\x0D\\x0Abash: line 1: " + Binary + ": No such file or directory\\x0A";

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(null, captured, string.Empty, Handshake("ended", captured));

        Assert.Equal(RemoteFailureKind.NotInstalled, failure.Kind);
    }

    [Fact]
    public void An_unsupported_reason_is_cut_to_200_characters()
    {
        string line = "/x/ntilde-mux: Exec format error " + new string('z', 400);

        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(126, string.Empty, line, null);

        Assert.Equal(RemoteFailureKind.Unsupported, failure.Kind);
        Assert.Equal(line[..200], failure.Reason);
    }

    [Fact]
    public void A_start_failure_is_SshFailed()
    {
        RemoteMuxFailure failure = RemoteMuxFailureClassifier.StartFailed(new FileNotFoundException("Unable to locate system OpenSSH executable (ssh)."));

        Assert.Equal(RemoteFailureKind.SshFailed, failure.Kind);
        Assert.Contains("Unable to locate system OpenSSH executable (ssh).", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exception_carries_the_failure_and_its_reason()
    {
        var failure = new RemoteMuxFailure(RemoteFailureKind.NotInstalled, "ntilde-mux is not installed");
        var inner = new IOException("x");

        var ex = new RemoteMuxUnavailableException(failure, inner);

        Assert.Same(failure, ex.Failure);
        Assert.Equal("ntilde-mux is not installed", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    private static MuxProxyHandshakeException Handshake(string reason, string captured = "") => new(reason, captured);
}
