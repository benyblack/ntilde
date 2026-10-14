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

    /// <summary>
    /// OpenSSH meeting a host key it does not know (StrictHostKeyChecking=ask): the askpass helper refuses "Are you sure you
    /// want to continue connecting" (or batch mode answers no for it), and ssh ends before signing in, with exit 255.
    /// </summary>
    internal const string UnknownHostKeyStderr = "Host key verification failed.\r\n";

    /// <summary>The same with StrictHostKeyChecking=yes: ssh says why first.</summary>
    internal const string StrictUnknownHostKeyStderr =
        "No ED25519 host key is known for fake-host and you have requested strict checking.\r\nHost key verification failed.\r\n";

    private const string ChangedHostKeyBanner =
        "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n"
        + "@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\r\n"
        + "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n"
        + "IT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!\r\n"
        + "Someone could be eavesdropping on you right now (man-in-the-middle attack)!\r\n"
        + "It is also possible that a host key has just been changed.\r\n"
        + "The fingerprint for the ED25519 key sent by the remote host is\r\n"
        + "SHA256:Zmx2b2hYc0h2a1hQbE5yN0x1T3FqY2tZbE5yN0x1T3E.\r\n"
        + "Please contact your system administrator.\r\n"
        + "Add correct host key in /home/nova/.ssh/known_hosts to get rid of this message.\r\n"
        + "Offending ED25519 key in /home/nova/.ssh/known_hosts:3\r\n"
        + "  remove with:\r\n"
        + "  ssh-keygen -f '/home/nova/.ssh/known_hosts' -R 'fake-host'\r\n";

    /// <summary>OpenSSH meeting a host key that changed since it was trusted (StrictHostKeyChecking=ask or yes): it refuses on its own.</summary>
    internal const string ChangedHostKeyStderr =
        ChangedHostKeyBanner
        + "Host key for fake-host has changed and you have requested strict checking.\r\n"
        + "Host key verification failed.\r\n";

    /// <summary>The changed key's strict-checking line without the banner before it: either is enough.</summary>
    private const string ChangedHostKeyWithoutBannerStderr =
        "Offending ED25519 key in /home/nova/.ssh/known_hosts:3\r\n"
        + "Host key for fake-host has changed and you have requested strict checking.\r\n"
        + "Host key verification failed.\r\n";

    /// <summary>
    /// A changed host key under StrictHostKeyChecking=no: ssh goes on, with password and keyboard-interactive sign-in off, so
    /// only a key could get in - here none did.
    /// </summary>
    internal const string ChangedHostKeyNotStrictStderr =
        ChangedHostKeyBanner
        + "Password authentication is disabled to avoid man-in-the-middle attacks.\r\n"
        + "Keyboard-interactive authentication is disabled to avoid man-in-the-middle attacks.\r\n"
        + "UpdateHostkeys is disabled because the host key is not trusted.\r\n"
        + "nova@fake-host: Permission denied (publickey).\r\n";

    private const string UnprotectedKeyStderr =
        "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n"
        + "@         WARNING: UNPROTECTED PRIVATE KEY FILE!          @\r\n"
        + "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n"
        + "Permissions 0644 for '/home/nova/.ssh/id_ed25519' are too open.\r\n"
        + "nova@fake-host: Permission denied (publickey).\r\n";

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

    /// <summary>
    /// Whether OpenSSH said the server refused the sign-in - the evidence a saved password it was handed was refused -
    /// automatic attempt or not: ssh's own final refusal, <c>[user@host: ]Permission denied (methods).</c>, or sshd's
    /// <c>Too many authentication failures</c>. A link that dropped, a host that was not there, is no such word; nor is a
    /// remote shell's "Permission denied" (a profile script, a file) that reached stderr before the link dropped. An
    /// automatic attempt is NeedsUser exactly when it is one.
    /// </summary>
    [Theory]
    [InlineData("nova@x: Permission denied (publickey,password).\r\n", true)]
    [InlineData("Permission denied (publickey,keyboard-interactive).\r\n", true)]   // OpenSSH before 7.x: no user@host
    [InlineData("nova@fe80::1: Permission denied (publickey).\r\n", true)]
    [InlineData("John Smith@x: Permission denied (password).\r\n", true)]   // ssh prints the server user as given: an AD name has a space
    [InlineData("x.sh: Permission denied (publickey).\r\n", false)]         // ssh's own prefix is always user@host
    [InlineData("Received disconnect from 10.0.0.2 port 22:2: Too many authentication failures\r\nDisconnected from 10.0.0.2 port 22\r\n", true)]
    [InlineData("Connection closed by 10.0.0.2 port 22\r\n", false)]
    [InlineData("ssh: connect to host x port 22: Connection refused\r\n", false)]
    [InlineData("-bash: /etc/profile.d/x.sh: Permission denied\r\nConnection to x closed by remote host.\r\n", false)]
    [InlineData("tool: Permission denied (os error 13)\r\nConnection to x closed by remote host.\r\n", false)]
    [InlineData("nova@x: Permission denied (publickey,password). Bye\r\n", false)]
    public void Says_whether_ssh_refused_the_sign_in(string stderr, bool refused)
    {
        RemoteMuxFailure automatic = RemoteMuxFailureClassifier.Classify(255, string.Empty, stderr, Handshake("ended"), "nova@x", automatic: true);
        RemoteMuxFailure user = RemoteMuxFailureClassifier.Classify(255, string.Empty, stderr, Handshake("ended"), "nova@x", automatic: false);

        Assert.Equal((refused, refused), (automatic.SignInRefused, user.SignInRefused));
        Assert.Equal(refused ? RemoteFailureKind.NeedsUser : RemoteFailureKind.SshFailed, automatic.Kind);
    }

    /// <summary>
    /// OpenSSH's own host-key failure ends ssh before sign-in, exit 255. A key it does not know, refused at its "Are you
    /// sure" (or in batch mode), is "Host key verification failed.": an automatic attempt would meet the same key every
    /// time, so it needs the user, with the host-key cause; a user's attempt stays an SSH failure, as it was, since its
    /// Enter shows the key. A key that changed is ssh's banner (REMOTE HOST IDENTIFICATION HAS CHANGED) and, under strict
    /// checking, "Host key for h has changed and you have requested strict checking." - either line is enough. ssh asks
    /// nothing about it, so Enter cannot show it either: any attempt, a user's too, needs the user, with the changed-key
    /// cause. Where ssh goes on (StrictHostKeyChecking=no) its refusal still sets SignInRefused, as before. The reason, for
    /// the log, is still ssh's last line. Lines are parsed whole: another banner, or the words inside a remote shell's
    /// line, are not it.
    /// </summary>
    [Theory]
    [InlineData(UnknownHostKeyStderr, "unknown", false)]
    [InlineData(StrictUnknownHostKeyStderr, "unknown", false)]
    [InlineData(ChangedHostKeyStderr, "changed", false)]
    [InlineData(ChangedHostKeyWithoutBannerStderr, "changed", false)]
    [InlineData(ChangedHostKeyNotStrictStderr, "changed", true)]
    [InlineData(UnprotectedKeyStderr, "neither", true)]
    [InlineData("bash: line 1: Host key verification failed.: command not found\r\nConnection to x closed by remote host.\r\n", "neither", false)]
    [InlineData("echo WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!\r\nConnection to x closed by remote host.\r\n", "neither", false)]
    [InlineData("echo Host key for x has changed and you have requested strict checking.\r\nConnection to x closed by remote host.\r\n", "neither", false)]
    public void Ssh_s_own_host_key_failure_needs_the_user(string stderr, string hostKey, bool signInRefused)
    {
        RemoteMuxFailure automatic = RemoteMuxFailureClassifier.Classify(255, string.Empty, stderr, Handshake("ended"), "nova@x", automatic: true);
        RemoteMuxFailure user = RemoteMuxFailureClassifier.Classify(255, string.Empty, stderr, Handshake("ended"), "nova@x", automatic: false);

        switch (hostKey)
        {
            case "unknown":
                Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.HostKey), (automatic.Kind, automatic.Cause));
                Assert.Equal(RemoteFailureKind.SshFailed, user.Kind);
                break;
            case "changed":
                Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.HostKeyChanged), (automatic.Kind, automatic.Cause));
                Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.HostKeyChanged), (user.Kind, user.Cause));
                break;
            default:
                Assert.DoesNotContain(automatic.Cause, new[] { RemoteNeedsUserCause.HostKey, RemoteNeedsUserCause.HostKeyChanged });
                Assert.Equal(RemoteFailureKind.SshFailed, user.Kind);
                break;
        }

        Assert.Equal((signInRefused, signInRefused), (automatic.SignInRefused, user.SignInRefused));
        Assert.Equal(user.Reason, automatic.Reason);
    }

    // Release hardening item 7: a listing's --no-spawn proxy, and an older ntilde-mux refusing it.
    [Fact]
    public void A_no_spawn_proxy_with_no_daemon_is_not_running()
    {
        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(5, string.Empty, "mux: No multiplexer is running.\n", Handshake("ended"), "nova@x", automatic: true);

        Assert.Equal(RemoteFailureKind.NotRunning, failure.Kind);
    }

    [Theory]
    [InlineData("Usage:\n  ntilde-mux proxy --stdio\n", true)]
    [InlineData("sh: 1: Syntax error: Unterminated quoted string\n", false)]   // sh's exit 2 is not ntilde-mux's
    public void Exit_2_is_an_older_proxy_only_with_its_usage(string stderr, bool tooOld)
    {
        Assert.Equal(tooOld ? RemoteFailureKind.ProxyTooOld : RemoteFailureKind.ProxyFailed, RemoteMuxFailureClassifier.Classify(2, string.Empty, stderr, Handshake("ended"), "nova@x", automatic: true).Kind);
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
    public void Server_text_in_a_failure_reason_loses_control_and_bidi_characters()
    {
        // ESC, BEL, a right-to-left override (Cf, which IsControl misses), a supplementary-plane tag character
        // (Cf above U+FFFF), an unpaired surrogate, a line separator and a CR.
        RemoteMuxFailure failure = RemoteMuxFailureClassifier.Classify(
            255, string.Empty, "x\u001b[2J\u0007y\u202Ez\U000E0041w\uD800v\u2028u\r", null);

        foreach (string bad in new[] { "\u001b", "\u0007", "\u202E", "\U000E0041", "\uD800", "\u2028", "\r" })
        {
            Assert.DoesNotContain(bad, failure.Reason, StringComparison.Ordinal);
        }

        foreach (string kept in new[] { "x", "y", "z", "w", "v", "u" })
        {
            Assert.Contains(kept, failure.Reason, StringComparison.Ordinal);
        }

        Assert.DoesNotContain('\u001b', Ntilde.Controls.TerminalPane.RemoteMuxUnavailableMessage("box", "a\u001bb"));
        Assert.DoesNotContain('\u202E', Ntilde.Controls.TerminalPane.RemoteMuxUnavailableMessage("box", "a\u202Eb"));
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
