namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// Where the install flow puts <c>ntilde-mux</c> on the remote host: <c>&lt;data&gt;/ntilde/bin</c>, with
/// <c>&lt;data&gt;</c> the host's <c>$XDG_DATA_HOME</c> when it is set and absolute, else
/// <c>$HOME/.local/share</c>. That is the rule the daemon's own root follows on Linux (.NET's
/// <c>LocalApplicationData</c> ignores an unset, empty or relative <c>XDG_DATA_HOME</c>). macOS sets no
/// <c>XDG_DATA_HOME</c>, so it keeps <c>~/.local/share</c> (its <c>Application Support</c> has a space).
/// </summary>
internal static class RemoteInstallDir
{
    /// <summary>
    /// POSIX sh that sets <c>$d</c> to the install directory. It holds no single quote and no backslash, so it
    /// sits inside <c>sh -c '…'</c> and passes through fish, tcsh and nushell untouched.
    /// </summary>
    internal const string Assign = "case \"${XDG_DATA_HOME-}\" in /*) d=\"$XDG_DATA_HOME/ntilde/bin\";; *) d=\"$HOME/.local/share/ntilde/bin\";; esac; ";

    /// <summary>The directory, for the UI copy that tells the user where the install goes.</summary>
    internal const string Display = "$XDG_DATA_HOME/ntilde/bin (default ~/.local/share/ntilde/bin)";
}
