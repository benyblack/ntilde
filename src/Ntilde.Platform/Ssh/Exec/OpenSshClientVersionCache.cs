namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The version of each OpenSSH client the app runs (<see cref="OpenSshClientVersion"/>), probed once per executable: per
/// full path and last-write time, so an ssh upgraded in place, or another one first on the PATH, is probed again. A
/// probe that failed is remembered too, as no version, until the executable changes. Lookups at once for one executable
/// share one probe; the others wait for it.
/// </summary>
public sealed class OpenSshClientVersionCache
{
    private readonly Func<string, Version?> _probe;
    private readonly Func<string, DateTime> _lastWriteTimeUtc;
    private readonly object _gate = new();
    // Guarded by _gate: each executable's last probe, by full path.
    private readonly Dictionary<string, (DateTime Stamp, Lazy<Version?> Version)> _probed =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <param name="probe">Reads the version of the client at a path; <see cref="OpenSshClientVersion.Probe(string)"/> by default.</param>
    /// <param name="lastWriteTimeUtc">An executable's last-write time; <see cref="File.GetLastWriteTimeUtc(string)"/> by default.</param>
    public OpenSshClientVersionCache(Func<string, Version?>? probe = null, Func<string, DateTime>? lastWriteTimeUtc = null)
    {
        _probe = probe ?? OpenSshClientVersion.Probe;
        _lastWriteTimeUtc = lastWriteTimeUtc ?? File.GetLastWriteTimeUtc;
    }

    /// <summary>The app's: every ssh it runs, for the process's life.</summary>
    public static OpenSshClientVersionCache Shared { get; } = new();

    /// <summary>
    /// The version of the client at <paramref name="sshExecutablePath"/>, or null when it cannot be read. The probe runs
    /// that path as given - the one the attempt runs - the first time it is looked up with its current last-write time, and
    /// may block for as long as the probe does: never on the UI thread.
    /// </summary>
    /// <param name="firstLookup">True for the lookup that found no probe of this executable as it is now, and started one.</param>
    public Version? VersionOf(string sshExecutablePath, out bool firstLookup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshExecutablePath);

        string key;
        DateTime stamp;
        try
        {
            key = Path.GetFullPath(sshExecutablePath);
            stamp = _lastWriteTimeUtc(key);
        }
        catch (Exception)
        {
            // Not a path the file system takes: nothing could run there either.
            firstLookup = false;
            return null;
        }

        Lazy<Version?> version;
        lock (_gate)
        {
            firstLookup = !_probed.TryGetValue(key, out var known) || known.Stamp != stamp;
            if (firstLookup)
            {
                known = (stamp, new Lazy<Version?>(() => ProbeOrNothing(sshExecutablePath)));
                _probed[key] = known;
            }

            version = known.Version;
        }

        return version.Value;
    }

    private Version? ProbeOrNothing(string sshExecutablePath)
    {
        try
        {
            return _probe(sshExecutablePath);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
