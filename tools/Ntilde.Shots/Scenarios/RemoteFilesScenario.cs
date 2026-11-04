using System.Reflection;
using Avalonia.Controls;
using Ntilde.Controls;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Services.Ssh;
using Ntilde.Shell;
using Ntilde.Shell.Secrets;

namespace Ntilde.Shots.Scenarios;

/// <summary>
/// A native SSH tab to <c>dev@edge-01</c> with the remote files sidebar open, then the same window
/// with the transfer centre showing a download and an upload in flight beside a finished one.
/// </summary>
/// <remarks>
/// Everything on screen is real: the session is a live native SSH connection to
/// <see cref="DemoSshHost"/>'s container, the sidebar lists that machine's home directory over it,
/// and the transfers are native SFTP jobs moving real bytes, captured part way through. That is why
/// this scenario needs Docker, and why it was left unregistered until there was a server to reach.
/// </remarks>
internal sealed class RemoteFilesScenario : IScenario
{
    /// <summary>Big enough that both large transfers are still running when the capture is taken.</summary>
    private const long UploadBytes = 1_400L * 1024 * 1024;

    public ShotSpec Spec { get; } = new(
        Name: "remote-files",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "Two images. 'remote-files' shows a native SSH tab to dev@edge-01 with the remote files " +
                "sidebar open beside it, listing the remote home directory's folders and files. " +
                "'remote-files-transfers' shows the same window with the transfer centre open: one " +
                "finished download and a large download and upload part way through, with progress " +
                "bars and byte counts.");

    public async Task RunAsync(ShotContext context)
    {
        using DemoSshHost host = DemoSshHost.Start(context.World);
        UseHarnessSshPrompts(context);

        TerminalPane pane = context.OpenTab(host.Profile);
        context.Driver.WaitFor(
            () => ScreenText(pane).Contains("dev@edge-01", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30),
            "the remote shell's prompt on edge-01");

        await context.RunCommandAsync(pane, "ls -lh logs releases/0.12.4 releases/0.13.0");

        pane.ToggleRemoteFilesSidebar();
        context.Driver.WaitFor(
            () => FindInTree<ListBox>(pane, "RemoteEntriesList") is { ItemCount: > 0 },
            TimeSpan.FromSeconds(20),
            "the remote files sidebar to list edge-01's home directory over the live session");
        context.Driver.Pump(5);

        context.Capture();

        Guid sessionId = pane.Session?.Id ?? throw new InvalidOperationException("The SSH pane has no session.");
        string downloads = Path.Combine(context.World.HomeRoot, "Downloads");
        Directory.CreateDirectory(downloads);

        TransferJob readme = Enqueue(context, host, sessionId, TransferDirection.Download,
            Path.Combine(downloads, "README.md"), $"{DemoSshHost.RemoteHome}/README.md");
        context.Driver.WaitFor(
            () => readme.State == TransferState.Completed,
            TimeSpan.FromSeconds(20),
            "the small README download to finish");

        // Sparse, so creating it costs nothing; the SFTP client still reads and sends every byte.
        string dist = Path.Combine(context.World.HomeRoot, "dist");
        Directory.CreateDirectory(dist);
        string upload = Path.Combine(dist, "ntilde-0.13.1-osx-arm64.dmg");
        using (var file = new FileStream(upload, FileMode.Create, FileAccess.Write))
        {
            file.SetLength(UploadBytes);
        }

        TransferJob download = Enqueue(context, host, sessionId, TransferDirection.Download,
            Path.Combine(downloads, "ntilde-0.13.0-full-images.tar"),
            $"{DemoSshHost.RemoteHome}/releases/0.13.0/ntilde-0.13.0-full-images.tar");
        TransferJob uploadJob = Enqueue(context, host, sessionId, TransferDirection.Upload,
            upload, $"{DemoSshHost.RemoteHome}/releases/ntilde-0.13.1-osx-arm64.dmg");

        try
        {
            context.Driver.WaitFor(
                () => InFlight(download) && InFlight(uploadJob),
                TimeSpan.FromSeconds(60),
                "the large download and upload to both be running and part way through");

            context.Capture("transfers");
        }
        finally
        {
            SftpService.Instance.CancelJob(download.Id);
            SftpService.Instance.CancelJob(uploadJob.Id);
            context.Driver.Pump(3);
        }
    }

    private static bool InFlight(TransferJob job) =>
        job.State == TransferState.Running && job.Progress is > 0.3 and < 0.9;

    private static TransferJob Enqueue(
        ShotContext context, DemoSshHost host, Guid sessionId, TransferDirection direction, string localPath, string remotePath)
    {
        var job = new TransferJob
        {
            SessionId = sessionId,
            ProfileId = host.Profile.Id,
            ProfileName = host.Profile.Name,
            Direction = direction,
            Kind = TransferKind.File,
            LocalPath = localPath,
            RemotePath = remotePath
        };

        context.Window.EnqueueTransferJob(job);
        context.Driver.Pump(2);
        return job;
    }

    /// <summary>
    /// Replaces the window's SSH prompt handler before the tab opens, so no dialog can appear in a
    /// headless run: the container's freshly generated host key is accepted (and recorded in the
    /// sandboxed known-hosts file), password prompts are refused (login is by key), and the vault is
    /// in memory rather than the real Keychain. MainWindow hands its handler to every pane it wires.
    /// </summary>
    private static void UseHarnessSshPrompts(ShotContext context)
    {
        FieldInfo field = typeof(MainWindow).GetField("_sshInteractionService", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow has no '_sshInteractionService' field. It was renamed - update the scenario.");

        field.SetValue(context.Window, new SshInteractionService(
            ownerProvider: () => context.Window,
            hostKeyPresenter: (_, _, _) => Task.FromResult(SshInteractionResponse.AcceptHostKey()),
            authPresenter: (_, _, _) => Task.FromResult(SshInteractionResponse.Cancel()),
            vaultService: new VaultService(new InMemorySecretStore())));
    }

    private static string ScreenText(TerminalPane pane) =>
        pane.Buffer is { } buffer
            ? string.Join('\n', buffer.ViewportRows.Select(row => new string(row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray())))
            : string.Empty;

    private static T? FindInTree<T>(Control root, string name) where T : Control =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().FirstOrDefault(c => c.Name == name);
}
