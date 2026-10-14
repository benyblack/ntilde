namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// A binary the user picked (Phase 4 spec §9 step 2(b)): for a host that cannot reach GitHub's release,
/// or a dev build that has none. Nothing vouches for it, so its own SHA-256 is what the dialog shows.
/// </summary>
/// <remarks>
/// Its header must say it was built for the host's RID (<see cref="MuxDaemonRid.Of"/>): a file for another
/// platform is refused here, before anything is uploaded. The upload's trial run
/// (<see cref="RemoteMuxInstallCommands.UploadForTrial"/>) is the second line of defence, for a file whose header
/// is right but which still cannot run, and the installer's check of what that run reports is the third, for
/// one that runs but is not this app's: it is discarded before it can replace the installed binary.
/// </remarks>
internal sealed class LocalFileMuxAssetSource(string path) : IMuxDaemonAssetSource
{
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="InvalidDataException">It is larger than <see cref="MuxDaemonAsset.MaxBytes"/>, or not an executable for <paramref name="rid"/>.</exception>
    public async Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct)
    {
        if (!MuxDaemonRid.IsKnown(rid)) throw new ArgumentException($"Not a published runtime identifier: \"{rid}\"", nameof(rid));

        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"{fullPath} does not exist.", fullPath);
        }

        if (info.Length > MuxDaemonAsset.MaxBytes)
        {
            throw new InvalidDataException($"{fullPath} is larger than {MuxDaemonAsset.MaxBytes / (1024 * 1024)} MiB: it is not an ntilde-mux binary.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        if (MuxDaemonRid.Of(bytes) != rid)
        {
            throw new InvalidDataException($"{Path.GetFileName(fullPath)} is {MuxDaemonRid.Describe(bytes)}; the host needs {rid}");
        }

        progress?.Report(bytes.Length);
        return new MuxDaemonAsset(bytes, MuxDaemonAsset.Sha256Of(bytes), fullPath);
    }
}
