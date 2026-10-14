namespace Ntilde.Mux.Contracts;

/// <summary>
/// Creates a directory only its owner can use: 0700 off Windows (Windows has ACLs, not POSIX modes). A folder of
/// session records, descriptors or sockets should not be listable by other users, and the umask default is
/// typically 0755.
/// </summary>
public static class PrivateDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates <paramref name="path"/> (and any missing parents; those get the default mode) owner-only off
    /// Windows. An existing directory is left untouched: judging it is its owner's job, not a writer's (the mux
    /// daemon refuses to serve from a socket directory that already exists with any other mode).
    /// </summary>
    public static DirectoryInfo Create(string path)
    {
        if (OperatingSystem.IsWindows() || Directory.Exists(path))
        {
            return Directory.CreateDirectory(path);
        }

        DirectoryInfo created = Directory.CreateDirectory(path, OwnerOnly);
        // CreateDirectory's mode is filtered by the umask, which can only remove bits: re-assert it.
        File.SetUnixFileMode(path, OwnerOnly);
        return created;
    }
}
