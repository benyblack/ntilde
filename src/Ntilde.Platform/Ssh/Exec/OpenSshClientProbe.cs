namespace Ntilde.Platform.Ssh.Exec;

/// <summary>What one probe of an OpenSSH client learned (<see cref="OpenSshClientVersion.Probe(string)"/>).</summary>
public enum OpenSshClientProbeKind
{
    /// <summary>
    /// Nothing: it did not start, did not exit within the timeout, or its output could not be read. The next probe may
    /// answer, so this one is not kept. The default.
    /// </summary>
    Indeterminate,

    /// <summary>It ran, exited 0, and printed OpenSSH's version (<see cref="OpenSshClientProbe.Version"/>).</summary>
    Version,

    /// <summary>
    /// It ran and exited, but not as an OpenSSH client: a non-zero exit, or no OpenSSH version in what it printed. That
    /// holds for the executable as it is.
    /// </summary>
    NotOpenSsh,
}

/// <summary>One probe's answer: its <paramref name="Kind"/>, the version when it found one, and why not otherwise.</summary>
/// <param name="Kind">What the probe learned.</param>
/// <param name="Version">The client's major and minor version; only with <see cref="OpenSshClientProbeKind.Version"/>.</param>
/// <param name="Reason">Why there is no version (<c>it exited with code 1</c>, <c>it did not exit within 5 s</c>), for the log.</param>
public readonly record struct OpenSshClientProbe(OpenSshClientProbeKind Kind, Version? Version, string Reason)
{
    /// <summary>A version, or definitely none: what holds until the executable changes.</summary>
    public bool IsDefinitive => Kind != OpenSshClientProbeKind.Indeterminate;

    public static OpenSshClientProbe Found(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(OpenSshClientProbeKind.Version, version, string.Empty);
    }

    public static OpenSshClientProbe NotOpenSsh(string reason) => new(OpenSshClientProbeKind.NotOpenSsh, null, reason);

    public static OpenSshClientProbe Indeterminate(string reason) => new(OpenSshClientProbeKind.Indeterminate, null, reason);
}
