using System;
using System.IO;
using System.Threading;
using Ntilde.Mux.Contracts;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Shell;

namespace Ntilde;

/// <summary>What the askpass helper did for one ssh process (one session token): see <see cref="SshAskPassSessionMarkers"/>.</summary>
/// <param name="Answered">It filled the target's password from the vault.</param>
/// <param name="Declined">
/// Vault-only, it declined a prompt that names the target and asks for more than the saved password - one that asks for
/// no password (a second factor), or the same keyboard-interactive round asking for a password again after the fill (an
/// expired password's new one) - which ssh then sent empty.
/// </param>
internal readonly record struct SshAskPassRecord(bool Answered, bool Declined);

/// <summary>
/// The askpass helper's record of what it did for each ssh process, one empty file per fact, named by the process's
/// session token (<c>NTILDE_SSH_ASKPASS_SESSION</c>): <c>&lt;token&gt;.answered</c> when it filled the target's password
/// from the vault, <c>&lt;token&gt;.declined</c> when, vault-only, it declined a prompt that names the target and asks for
/// more (<see cref="SshAskPassRecord.Declined"/>). Every attempt fills at most once per token (<see cref="TryClaim"/>): a
/// second prompt from the same ssh goes to the dialog on a user's attempt, and gets no answer on an automatic one. An
/// automatic attempt's connector reads the record back (<see cref="Read"/>) to tell a refused saved password from one
/// that was never asked for, or from a second factor.
/// The helper is a new process for every prompt, so the record lives on disk, in the app's data folder (per user, and not
/// part of a backup). Nothing secret is written: a file's name is the token, and it is empty.
/// </summary>
internal sealed class SshAskPassSessionMarkers
{
    /// <summary>How old a record may get before a later one sweeps it: far past any one ssh's sign-in.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private const string AnsweredExtension = ".answered";
    private const string DeclinedExtension = ".declined";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Lazy<string> _directory;
    private readonly Action<string>? _log;
    private readonly Action<string, UnixFileMode> _setMode;
    private int _refusalLogged; // 1 once a folder that cannot be made private was logged

    /// <param name="directory">Where the records go; read only when one is looked up or written.</param>
    /// <param name="log">Where a folder that cannot be made private is reported, once (<see cref="PrepareFolder"/>).</param>
    /// <param name="setMode">chmod, off Windows; the seam a test makes fail.</param>
    public SshAskPassSessionMarkers(Func<string> directory, Action<string>? log = null, Action<string, UnixFileMode>? setMode = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = new Lazy<string>(directory);
        _log = log;
        _setMode = setMode ?? ((path, mode) =>
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
        });
    }

    /// <summary>The app's own folder for them: <c>askpass</c> under the app-data root.</summary>
    internal static string DefaultDirectory => Path.Combine(AppPaths.RootDirectory, "askpass");

    /// <summary>Whether the helper already filled the target's password from the vault for this ssh.</summary>
    public bool HasAnswered(string token) => Exists(token, AnsweredExtension);

    /// <summary>
    /// Claims this ssh's one fill from the vault: true only for the call that creates the record - created atomically, so
    /// of two racing calls one wins. False when it exists already (filled before) or cannot be written; either way the
    /// caller does not fill - a user's attempt asks the user, an automatic one gives no answer - rather than risk sending a
    /// refused password again. Sweeps stale records.
    /// </summary>
    public bool TryClaim(string token) => Create(token, AnsweredExtension, claim: true);

    /// <summary>Vault-only: records that the helper declined a prompt naming the target that asks for more than the saved password.</summary>
    public void RecordDeclined(string token) => Create(token, DeclinedExtension, claim: false);

    /// <summary>
    /// Whether records can be written now (Greptile G1): the folder exists or can be created, is private (<see cref="PrepareFolder"/>), and takes a new file - a probe
    /// created and deleted at once. An automatic attempt offers the saved password only then: without its record, a refused
    /// saved password could not be counted, and every later attempt would send it again.
    /// </summary>
    public bool CanRecord()
    {
        try
        {
            if (!PrepareFolder()) return false;
            string probe = Path.Combine(_directory.Value, "probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>What the helper did for this ssh, as far as its records say; nothing for a token with no record.</summary>
    public SshAskPassRecord Read(string token) => new(Exists(token, AnsweredExtension), Exists(token, DeclinedExtension));

    private bool Exists(string token, string extension)
    {
        RequireToken(token);
        try
        {
            return File.Exists(PathOf(token, extension));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <returns>True when this call created the record; with <paramref name="claim"/> false, also when it was there already.</returns>
    private bool Create(string token, string extension, bool claim)
    {
        RequireToken(token);
        try
        {
            if (!PrepareFolder()) return false;
            using (new FileStream(PathOf(token, extension), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
            }
        }
        catch (IOException) when (!claim && File.Exists(PathOf(token, extension)))
        {
            return true;   // recorded before: the fact stands
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;  // there already (a claim lost), or not writable
        }

        SweepStale();
        return true;
    }

    /// <summary>
    /// Creates the folder (0700 when new, <see cref="PrivateDirectory.Create"/>) and, off Windows, makes sure it is private
    /// before a record goes in (Codex review of PR #511, P2). An upgrade usually finds the folder an earlier build made under
    /// the umask, often 0755, and <see cref="PrivateDirectory"/> leaves an existing folder as it is: judging it is its
    /// owner's job, and the askpass records are this class's. So: a symbolic link in its place is refused, never followed;
    /// a mode other than exactly 0700 is set to 0700 and read back. chmod works only for the owner (or root), so a failure
    /// means the folder is not ours. Then, or on any failure, false: nothing is written there, logged once. Every caller
    /// already treats that as "cannot record": the app offers no saved password to an automatic reconnect, and the helper
    /// fills nothing from the vault, so there is no evidence either way - never "answered", never "declined".
    /// </summary>
    private bool PrepareFolder()
    {
        string folder = _directory.Value;
        if (OperatingSystem.IsWindows())
        {
            PrivateDirectory.Create(folder);
            return true;
        }

        try
        {
            // Before anything that follows a link: Directory.Exists (in PrivateDirectory.Create) and chmod both do.
            if (IsLink(folder)) return Refuse($"{folder} is a symbolic link");
            PrivateDirectory.Create(folder);
            if (IsLink(folder)) return Refuse($"{folder} is a symbolic link");

            UnixFileMode mode = File.GetUnixFileMode(folder);
            if (mode == OwnerOnly) return true;
            _setMode(folder, OwnerOnly);
            mode = File.GetUnixFileMode(folder);
            return mode == OwnerOnly || Refuse($"{folder} still has mode {Convert.ToString((int)mode, 8)} after a chmod to 700");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Refuse($"{folder} could not be made private (mode 700): {ex.Message}");
        }

        static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;
    }

    private bool Refuse(string why)
    {
        if (Interlocked.Exchange(ref _refusalLogged, 1) == 0)
        {
            _log?.Invoke($"[askpass] {why}; no askpass record is written there, and without one the saved password is not filled from the vault");
        }

        return false;
    }

    private static void RequireToken(string token)
    {
        if (!SshAskPassEnvironment.IsSessionToken(token)) throw new ArgumentException("Not a session token.", nameof(token));
    }

    private string PathOf(string token, string extension) => Path.Combine(_directory.Value, token + extension);

    /// <summary>Deletes the records older than <see cref="StaleAfter"/>; one that cannot be deleted stays for the next sweep.</summary>
    private void SweepStale()
    {
        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory.Value))
            {
                if (!file.EndsWith(AnsweredExtension, StringComparison.Ordinal) && !file.EndsWith(DeclinedExtension, StringComparison.Ordinal)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another helper may be sweeping too.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sweeping is housekeeping only.
        }
    }
}
