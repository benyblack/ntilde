namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// A binary the user picked (Phase 4 spec §9 step 2(b)): for a host that cannot reach GitHub's release,
/// or a dev build that has none. Nothing vouches for it, so its own SHA-256 is what the dialog shows.
/// </summary>
/// <remarks>
/// It cannot know which platform the file was built for, so the RID is not checked here. The upload
/// runs the file once before it replaces anything (<see cref="RemoteMuxInstallCommands.Upload"/>), so a
/// file for the wrong platform fails there and leaves an installed binary in place.
/// </remarks>
internal sealed class LocalFileMuxAssetSource(string path) : IMuxDaemonAssetSource
{
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="InvalidDataException">It is larger than <see cref="MuxDaemonAsset.MaxBytes"/>.</exception>
    public async Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct)
    {
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
        progress?.Report(bytes.Length);
        return new MuxDaemonAsset(bytes, MuxDaemonAsset.Sha256Of(bytes), fullPath);
    }
}
