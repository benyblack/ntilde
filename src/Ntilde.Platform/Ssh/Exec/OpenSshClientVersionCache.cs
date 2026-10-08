namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// What each OpenSSH client the app runs is (<see cref="OpenSshClientVersion"/>), probed once per executable: per full
/// path and last-write time, so an ssh upgraded in place, or another one first on the PATH, is probed again. Only a
/// definitive answer is kept - a version, or definitely not OpenSSH. An indeterminate one (a timeout, a start that
/// failed) is the answer for the lookups that shared that probe, and the next lookup probes again: a slow moment, such as
/// many hosts reconnecting after a resume, must not cost a healthy client its version for the process's life. Lookups at
/// once for one executable share one probe in flight; the others wait for it.
/// </summary>
public sealed class OpenSshClientVersionCache
{
    private readonly Func<string, OpenSshClientProbe> _probe;
    private readonly Func<string, DateTime> _lastWriteTimeUtc;
    private readonly object _gate = new();
    // Guarded by _gate: each executable's probe - definitive, or still running - by full path.
    private readonly Dictionary<string, (DateTime Stamp, Lazy<OpenSshClientProbe> Probe)> _probed =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <param name="probe">Probes the client at a path; <see cref="OpenSshClientVersion.Probe(string)"/> by default.</param>
    /// <param name="lastWriteTimeUtc">An executable's last-write time; <see cref="File.GetLastWriteTimeUtc(string)"/> by default.</param>
    public OpenSshClientVersionCache(Func<string, OpenSshClientProbe>? probe = null, Func<string, DateTime>? lastWriteTimeUtc = null)
    {
        _probe = probe ?? OpenSshClientVersion.Probe;
        _lastWriteTimeUtc = lastWriteTimeUtc ?? File.GetLastWriteTimeUtc;
    }

    /// <summary>The app's: every ssh it runs, for the process's life.</summary>
    public static OpenSshClientVersionCache Shared { get; } = new();

    /// <summary>
    /// Told each time a lookup takes the probe another lookup started, running or kept: lets a test hold a probe until the
    /// lookups it expects have joined it, rather than guess how long they take to arrive.
    /// </summary>
    internal Action? Joined { get; init; }

    /// <summary>
    /// What the client at <paramref name="sshExecutablePath"/> is. Unless a definitive answer is kept for it as it is now,
    /// or a probe of it is running, this lookup probes it: the probe runs that path as given - the one the attempt runs -
    /// and may block for as long as the probe does, so never on the UI thread.
    /// </summary>
    /// <param name="probedHere">True for the lookup that started the probe whose answer this is: the one to log it.</param>
    public OpenSshClientProbe Lookup(string sshExecutablePath, out bool probedHere)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshExecutablePath);

        string key;
        DateTime stamp;
        try
        {
            key = Path.GetFullPath(sshExecutablePath);
            stamp = _lastWriteTimeUtc(key);
        }
        catch (Exception ex)
        {
            // Not a path the file system takes: nothing could run there either.
            probedHere = true;
            return OpenSshClientProbe.Indeterminate($"its path cannot be read: {ex.Message}");
        }

        Lazy<OpenSshClientProbe> probe;
        lock (_gate)
        {
            probedHere = !_probed.TryGetValue(key, out var known) || known.Stamp != stamp;
            if (probedHere)
            {
                known = (stamp, new Lazy<OpenSshClientProbe>(() => ProbeOrIndeterminate(sshExecutablePath)));
                _probed[key] = known;
            }

            probe = known.Probe;
        }

        if (!probedHere) Joined?.Invoke();
        OpenSshClientProbe answer = probe.Value;
        if (!answer.IsDefinitive)
        {
            lock (_gate)
            {
                // Not kept; only this probe goes, never a newer one another lookup has started since.
                if (_probed.TryGetValue(key, out var current) && ReferenceEquals(current.Probe, probe)) _probed.Remove(key);
            }
        }

        return answer;
    }

    private OpenSshClientProbe ProbeOrIndeterminate(string sshExecutablePath)
    {
        try
        {
            return _probe(sshExecutablePath);
        }
        catch (Exception ex)
        {
            return OpenSshClientProbe.Indeterminate($"the probe failed: {ex.Message}");
        }
    }
}
