using System;
using System.IO;
using Ntilde.Shell;

namespace Ntilde;

/// <summary>
/// The askpass helper's record of the ssh processes whose target password it already filled from the vault: one empty
/// file per session token (<c>NTILDE_SSH_ASKPASS_SESSION</c>, new for each ssh the exec transport starts), so a second
/// prompt from the same ssh - the saved password was refused - goes to the user's dialog instead of sending it again.
/// The helper is a new process for every prompt, so the record lives on disk, in the app's data folder (per user, and
/// not part of a backup). Nothing secret is written: a file's name is the token, and it is empty.
/// </summary>
internal sealed class SshAskPassSessionMarkers
{
    /// <summary>How old a record may get before a later fill sweeps it: far past any one ssh's sign-in.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private const string Extension = ".answered";

    private readonly Lazy<string> _directory;

    /// <param name="directory">Where the records go; read only when one is looked up or written.</param>
    public SshAskPassSessionMarkers(Func<string> directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = new Lazy<string>(directory);
    }

    /// <summary>The app's own folder for them: <c>askpass</c> under the app-data root.</summary>
    internal static string DefaultDirectory => Path.Combine(AppPaths.RootDirectory, "askpass");

    /// <summary>
    /// Whether <paramref name="token"/> is one the transport writes: 32 lowercase hex digits. Anything else - a path, an
    /// empty value - names no file, and the helper behaves as for an ssh with no token.
    /// </summary>
    internal static bool IsToken(string? token)
    {
        if (token is not { Length: 32 }) return false;
        foreach (char c in token)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')) return false;
        }

        return true;
    }

    /// <summary>Whether the helper already filled the target's password from the vault for this ssh.</summary>
    public bool HasAnswered(string token)
    {
        if (!IsToken(token)) throw new ArgumentException("Not a session token.", nameof(token));
        try
        {
            return File.Exists(PathOf(token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records that the helper fills the target's password from the vault for this ssh: true once the record exists. False
    /// when it cannot be written - the caller then asks the user rather than risk sending a refused password again. Each
    /// record written sweeps those older than <see cref="StaleAfter"/>.
    /// </summary>
    public bool TryRecordAnswered(string token)
    {
        if (!IsToken(token)) throw new ArgumentException("Not a session token.", nameof(token));
        try
        {
            Directory.CreateDirectory(_directory.Value);
            using (new FileStream(PathOf(token), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        SweepStale();
        return true;
    }

    private string PathOf(string token) => Path.Combine(_directory.Value, token + Extension);

    /// <summary>Deletes the records older than <see cref="StaleAfter"/>; one that cannot be deleted stays for the next sweep.</summary>
    private void SweepStale()
    {
        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory.Value, "*" + Extension))
            {
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
