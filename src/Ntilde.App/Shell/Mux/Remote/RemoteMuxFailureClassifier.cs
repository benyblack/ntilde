using System.Globalization;
using Ntilde.Mux.Contracts;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// Turns a failed remote connect into a <see cref="RemoteMuxFailure"/> (Phase 4 spec §7.1): a pure
/// function of the channel's exit status, what the remote side printed, and the exception the attempt
/// ended with. The first rule that matches wins:
/// <list type="number">
/// <item>the hello's <c>version_mismatch</c> → <see cref="RemoteFailureKind.VersionMismatch"/>;</item>
/// <item>a native transport failure (an <see cref="SshExecTransportException"/>, directly or inside the
/// handshake's exception) → <see cref="RemoteFailureKind.SshFailed"/>, with the native message. The
/// remote command never ran, and the stderr tail then holds that message, not the command's output, so
/// it is not matched against the text rules below;</item>
/// <item>a loader or format error on any line (<c>Exec format error</c>, <c>cannot execute binary
/// file</c>, <c>GLIBC_x' not found</c>, <c>version `GLIBC</c>, musl's <c>Error loading shared
/// library</c>) → <see cref="RemoteFailureKind.Unsupported"/>, with that line. Checked before the
/// not-installed rule: the dynamic loader exits 127 too, and its line has "not found" or "No such
/// file" next to the binary's path;</item>
/// <item>exit 127, or a line naming <c>ntilde-mux</c> with "not found" or "No such file or
/// directory" → <see cref="RemoteFailureKind.NotInstalled"/>;</item>
/// <item>exit 126 → <see cref="RemoteFailureKind.Unsupported"/>, with the last stderr line;</item>
/// <item>exit 255 → <see cref="RemoteFailureKind.SshFailed"/>, with the last stderr line, whichever
/// backend ran it. It is OpenSSH's own failure; the remote command cannot be the source, since the proxy
/// exits only 0 to 4 (spec §8.1) and a shell that cannot run it exits 126 or 127. A refusal - ssh's own
/// <c>[user@host: ]Permission denied (methods).</c>, or sshd's <c>Too many authentication failures</c> - sets
/// <see cref="RemoteMuxFailure.SignInRefused"/>, and for an automatic attempt is
/// <see cref="RemoteFailureKind.NeedsUser"/> instead, with the same reason: in batch mode ssh tried only what
/// needs no answer, and with the saved password it tried that once, so signing in needs the user. So is ssh's
/// own host-key failure (<c>Host key verification failed.</c>), with the <see cref="RemoteNeedsUserCause.HostKey"/>
/// cause: every automatic attempt would meet the same key. A key that changed (ssh's warning banner, or its
/// strict-checking line) is <see cref="RemoteFailureKind.NeedsUser"/> for a user's attempt too, with the
/// <see cref="RemoteNeedsUserCause.HostKeyChanged"/> cause: ssh refuses it without asking, so Enter cannot show it;</item>
/// <item>anything else → <see cref="RemoteFailureKind.ProxyFailed"/>, with the exception's message
/// (the handshake's carries what the remote side printed) and the last stderr line, where the proxy
/// reports a daemon it could not reach.</item>
/// </list>
/// </summary>
internal static class RemoteMuxFailureClassifier
{
    public const string NotInstalledReason = "ntilde-mux is not installed";

    private const string BinaryName = "ntilde-mux";

    /// <param name="exitCode">The channel's exit status; null when unknown (killed, native failure, a signal).</param>
    /// <param name="capturedStdout">What the remote side printed before the greeting never came (<c>MuxProxyHandshakeException.CapturedText</c>, control characters escaped).</param>
    /// <param name="stderr">The channel's stderr tail.</param>
    /// <param name="error">What the attempt ended with.</param>
    /// <param name="host">The host, for the version-mismatch reason (<c>user@host</c>).</param>
    /// <param name="automatic">The attempt was automatic: nobody could be asked, and OpenSSH ran in batch mode or offered only the saved password.</param>
    public static RemoteMuxFailure Classify(int? exitCode, string capturedStdout, string stderr, Exception? error, string? host = null, bool automatic = false)
    {
        capturedStdout ??= string.Empty;
        stderr ??= string.Empty;

        if (Find<MuxProtocolException>(error, e => e.Code == MuxErrorCodes.VersionMismatch) is { } mismatch)
        {
            return new RemoteMuxFailure(RemoteFailureKind.VersionMismatch, VersionMismatchReason(mismatch.Message, host));
        }

        if (Find<SshExecTransportException>(error, _ => true) is { } transport)
        {
            string message = transport.NativeMessage.Trim().Length > 0 ? transport.NativeMessage.Trim() : transport.Message;
            return new RemoteMuxFailure(RemoteFailureKind.SshFailed, Quote(message));
        }

        List<string> lines = [.. Lines(stderr), .. CapturedLines(capturedStdout)];
        if (lines.Find(IsLoaderOrFormatError) is { } unsupported)
        {
            return new RemoteMuxFailure(RemoteFailureKind.Unsupported, Quote(unsupported));
        }

        if (exitCode == 127 || lines.Exists(IsBinaryNotFound))
        {
            return new RemoteMuxFailure(RemoteFailureKind.NotInstalled, NotInstalledReason);
        }

        string? lastStderrLine = Lines(stderr).LastOrDefault();
        if (exitCode == 126)
        {
            return new RemoteMuxFailure(RemoteFailureKind.Unsupported, Quote(lastStderrLine ?? "ntilde-mux cannot run there (exit 126)"));
        }

        if (exitCode == 255)
        {
            // Batch mode tried keys and the agent only; refused, it is a password or a passphrase away, which
            // only the user can give. Retrying on a timer would only knock again (Task 20 ruling). An attempt that
            // offered the saved password (BatchMode=no) was refused the same way, and must not send it again. sshd past
            // MaxAuthTries cuts the connection instead of refusing the last try: a refusal all the same. Any other exit 255 -
            // the link dropping, nothing listening - says nothing about what was sent.
            bool refused = Lines(stderr).Any(line =>
                IsSshPermissionDenied(line) || line.Contains("Too many authentication failures", StringComparison.Ordinal));
            // A host key the user does not trust is met again by every automatic attempt: the user has to review it. It
            // decides the cause even with a refusal after it - a changed key turns password sign-in off. A key that changed
            // needs the user on a user's attempt too: ssh refuses it without asking, so Enter cannot show it.
            bool changedKey = Lines(stderr).Any(IsSshChangedHostKey);
            bool hostKey = changedKey || Lines(stderr).Any(line => line == HostKeyVerificationFailed);
            RemoteFailureKind kind = changedKey || (automatic && (refused || hostKey)) ? RemoteFailureKind.NeedsUser : RemoteFailureKind.SshFailed;
            RemoteNeedsUserCause cause = kind != RemoteFailureKind.NeedsUser ? RemoteNeedsUserCause.SignIn
                : changedKey ? RemoteNeedsUserCause.HostKeyChanged
                : hostKey ? RemoteNeedsUserCause.HostKey
                : RemoteNeedsUserCause.SignIn;
            return new RemoteMuxFailure(kind, Quote(lastStderrLine ?? "ssh exited with code 255"), cause) { SignInRefused = refused };
        }

        string reason = error?.Message is { Length: > 0 } errorMessage ? errorMessage : "The ntilde-mux proxy failed";
        if (lastStderrLine is not null && !reason.Contains(lastStderrLine, StringComparison.Ordinal))
        {
            reason += $" ({Quote(lastStderrLine)})";
        }

        return new RemoteMuxFailure(RemoteFailureKind.ProxyFailed, reason);
    }

    /// <summary>
    /// The transport could not even start the command: no ssh executable, a profile it cannot use. A transport factory
    /// that refused the attempt with a failure of its own (a <see cref="RemoteMuxUnavailableException"/>: the native SSH
    /// switch off, <see cref="RemoteMuxHostFactory.ThrowIfNativeSshDisabled"/>) keeps it, kind and reason.
    /// </summary>
    public static RemoteMuxFailure StartFailed(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is RemoteMuxUnavailableException refused) return refused.Failure;
        return new RemoteMuxFailure(RemoteFailureKind.SshFailed, Quote($"SSH could not start: {error.Message}"));
    }

    /// <summary><paramref name="error"/> or the first exception in its inner chain that is a <typeparamref name="T"/> matching <paramref name="match"/>.</summary>
    private static T? Find<T>(Exception? error, Func<T, bool> match) where T : Exception
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is T candidate && match(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// <c>ntilde-mux on &lt;host&gt; speaks protocol a-b; this app speaks 1-2</c>. The daemon's range is
    /// read from the daemon's refusal (<c>Server speaks a..b; …</c>) or the client's own check
    /// (<c>Server chose protocol v, …</c>).
    /// </summary>
    private static string VersionMismatchReason(string message, string? host)
    {
        string where = string.IsNullOrWhiteSpace(host) ? "the remote host" : host;
        string app = string.Create(CultureInfo.InvariantCulture, $"{MuxProtocol.MinSupportedVersion}-{MuxProtocol.MaxSupportedVersion}");
        return DaemonProtocols(message) is { } daemon
            ? $"ntilde-mux on {where} speaks protocol {daemon}; this app speaks {app}"
            : $"ntilde-mux on {where} speaks another protocol version; this app speaks {app}";
    }

    private static string? DaemonProtocols(string message)
    {
        if (After(message, "Server speaks ") is { } range)
        {
            int min = LeadingNumber(range, out int used);
            if (min >= 0 && range.AsSpan(used).StartsWith("..", StringComparison.Ordinal))
            {
                int max = LeadingNumber(range[(used + 2)..], out _);
                if (max >= 0) return min == max ? Format(min) : Format(min) + "-" + Format(max);
            }
        }

        if (After(message, "Server chose protocol ") is { } chosen)
        {
            int version = LeadingNumber(chosen, out _);
            if (version >= 0) return Format(version);
        }

        return null;

        static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    private static string? After(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : text[(at + marker.Length)..];
    }

    /// <summary>The run of ASCII digits <paramref name="text"/> starts with, or -1; <paramref name="used"/> is its length.</summary>
    private static int LeadingNumber(string text, out int used)
    {
        used = 0;
        while (used < text.Length && char.IsAsciiDigit(text[used])) used++;
        return used > 0 && int.TryParse(text.AsSpan(0, used), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : -1;
    }

    private static bool IsLoaderOrFormatError(string line) =>
        line.Contains("Exec format error", StringComparison.Ordinal)
        || line.Contains("cannot execute binary file", StringComparison.Ordinal)
        || line.Contains("Error loading shared library", StringComparison.Ordinal)
        || HasGlibcVersionNotFound(line)
        || HasVersionGlibc(line);

    /// <summary><c>GLIBC_[0-9.]+' not found</c>.</summary>
    private static bool HasGlibcVersionNotFound(string line)
    {
        for (int at = line.IndexOf("GLIBC_", StringComparison.Ordinal); at >= 0; at = line.IndexOf("GLIBC_", at + 1, StringComparison.Ordinal))
        {
            int end = at + "GLIBC_".Length;
            int digits = end;
            while (digits < line.Length && (char.IsAsciiDigit(line[digits]) || line[digits] == '.')) digits++;
            if (digits > end && line.AsSpan(digits).StartsWith("' not found", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary><c>version .GLIBC</c>: any one character (a quote or backtick) between them.</summary>
    private static bool HasVersionGlibc(string line)
    {
        for (int at = line.IndexOf("version ", StringComparison.Ordinal); at >= 0; at = line.IndexOf("version ", at + 1, StringComparison.Ordinal))
        {
            int quote = at + "version ".Length;
            if (quote < line.Length && line.AsSpan(quote + 1).StartsWith("GLIBC", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool IsBinaryNotFound(string line) =>
        line.Contains(BinaryName, StringComparison.Ordinal)
        && (line.Contains("not found", StringComparison.Ordinal) || line.Contains("No such file or directory", StringComparison.Ordinal));

    /// <summary>
    /// ssh's own final refusal, as the whole line: <c>[user@host: ]Permission denied (method,...).</c> - the methods the
    /// server still offered, names with no space between them. It decides that a saved password counts as refused, so the
    /// line is parsed, not searched: a remote shell's <c>-bash: x.sh: Permission denied</c>, or a tool's <c>Permission
    /// denied (os error 13)</c>, that reached stderr before the link dropped is not one.
    /// </summary>
    private static bool IsSshPermissionDenied(string line)
    {
        const string Denied = "Permission denied (";
        int at = line.IndexOf(Denied, StringComparison.Ordinal);
        if (at < 0) return false;

        // Before it: nothing (OpenSSH before 7.x), or "user@host: ". The user is the server's, printed as given, so it
        // may hold a space (an AD name, "John Smith"); the "@" is ssh's own.
        ReadOnlySpan<char> target = line.AsSpan(0, at);
        if (target.Length > 0)
        {
            if (!target.EndsWith(": ", StringComparison.Ordinal)) return false;
            if (!target[..^2].Contains('@')) return false;
        }

        ReadOnlySpan<char> methods = line.AsSpan(at + Denied.Length);
        if (!methods.EndsWith(").", StringComparison.Ordinal)) return false;
        foreach (char c in methods[..^2])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or ',' or '@' or '.' or '_')) return false;
        }

        return true;
    }

    /// <summary>
    /// ssh's own host-key failure, as the whole line: a key it does not know, refused at its "Are you sure" (by the askpass
    /// helper, or batch mode), or one that changed under strict checking.
    /// </summary>
    private const string HostKeyVerificationFailed = "Host key verification failed.";

    private const string ChangedHostKeyPrefix = "Host key for ";
    private const string ChangedHostKeySuffix = " has changed and you have requested strict checking.";

    /// <summary>
    /// ssh's own word that a host key changed since it was trusted, as the whole line: the warning banner's
    /// <c>@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @</c>, which ssh prints even where it goes on
    /// (StrictHostKeyChecking=no, with password sign-in turned off), or, under strict checking,
    /// <c>Host key for h has changed and you have requested strict checking.</c> Either is enough. Parsed, not searched, as
    /// the refusal is.
    /// </summary>
    private static bool IsSshChangedHostKey(string line) =>
        (line.StartsWith('@') && line.EndsWith('@') && line.Trim('@', ' ', '\t') == "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!")
        || (line.Length > ChangedHostKeyPrefix.Length + ChangedHostKeySuffix.Length
            && line.StartsWith(ChangedHostKeyPrefix, StringComparison.Ordinal)
            && line.EndsWith(ChangedHostKeySuffix, StringComparison.Ordinal));

    /// <summary>The non-blank lines of <paramref name="text"/>, trimmed.</summary>
    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

    /// <summary>The captured stdout's lines: StdioMuxTransport writes each control character as <c>\xNN</c>.</summary>
    private static IEnumerable<string> CapturedLines(string captured) =>
        Lines(captured.Replace("\\x0D", string.Empty, StringComparison.Ordinal).Replace("\\x0A", "\n", StringComparison.Ordinal));

    /// <summary>Server-controlled text for a toast: trimmed, cut short, and without control or bidi characters.</summary>
    private static string Quote(string text) => RemoteOutputText.Cut(RemoteOutputText.Clean(text).Trim(), RemoteOutputText.MaxLength);
}
