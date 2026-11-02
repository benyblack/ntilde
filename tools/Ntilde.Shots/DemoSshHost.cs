using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Services.Ssh;
using Ntilde.Shell;

namespace Ntilde.Shots;

/// <summary>
/// The remote machine the remote-files scenario connects to: a throwaway sshd in Docker, built from
/// <c>Assets/demo-ssh/Dockerfile</c>, published on <c>edge-01.localhost:2222</c> for one scenario and
/// stopped (and, being <c>--rm</c>, deleted) when this is disposed.
/// </summary>
/// <remarks>
/// <para>
/// A real server, not a stand-in. The remote files sidebar lists over a genuinely connected native
/// SSH session, and the transfer centre shows bytes the native SFTP client is really moving; faking
/// either (the pane's test hooks substitute a fake directory browser) would show a feature working
/// that this run never exercised. The cost is that these scenarios need Docker - the only ones in the
/// catalogue that do - and fail with a plain message when it is not running.
/// </para>
/// <para>
/// <c>edge-01.localhost</c> because the sidebar prints <c>user@host:port</c>: any <c>*.localhost</c>
/// name resolves to loopback on its own, so the image reads as a named machine without touching
/// <c>/etc/hosts</c>. Port 2222 because 22 belongs to the capturing Mac's own Remote Login. Both
/// loopback addresses are published, since the name resolves to <c>::1</c> first.
/// </para>
/// <para>
/// Nothing of the developer's is used: login is by a key generated inside the container for this run
/// (agent auth off, so the real ssh-agent is never asked), the profile and the trusted host key are
/// written under the run's sandboxed app-data root, and no password exists to reach the Keychain.
/// </para>
/// </remarks>
internal sealed class DemoSshHost : IDisposable
{
    public const string HostName = "edge-01.localhost";
    public const int Port = 2222;
    public const string UserName = "dev";
    public const string RemoteHome = "/home/dev";

    // Bump with any change to Assets/demo-ssh: EnsureImage reuses whatever image already has this tag.
    private const string ImageTag = "ntilde-shots-ssh:v2";

    /// <summary>Fixed, so the tab and transfer rows a run produces are the same every time.</summary>
    private static readonly Guid ProfileId = Guid.Parse("6f1c9a52-3d0e-4b7a-9c35-0e2d7f9b1a64");

    private readonly string _containerName;

    /// <summary>The developer's real HOME, so the docker CLI finds its context; see DemoWorld.OriginalEnvironment.</summary>
    private static string? s_dockerHome;

    private DemoSshHost(string containerName, TerminalProfile profile)
    {
        _containerName = containerName;
        Profile = profile;
    }

    /// <summary>The saved native SSH profile for <c>dev@edge-01</c>.</summary>
    public TerminalProfile Profile { get; }

    public static DemoSshHost Start(DemoWorld world)
    {
        s_dockerHome = world.OriginalEnvironment("HOME");

        if (Docker("info --format {{.ServerVersion}}", throwOnError: false).ExitCode != 0)
        {
            throw new InvalidOperationException(
                "This scenario needs Docker running: it connects to a throwaway SSH server in a " +
                "container (see DemoSshHost). Start Docker Desktop, or leave the scenario out of the run.");
        }

        EnsureImage();

        string containerName = $"ntilde-shots-ssh-{Guid.NewGuid():N}"[..28];
        Docker(
            $"run -d --rm --name {containerName} --hostname edge-01 " +
            $"-p 127.0.0.1:{Port}:22 -p [::1]:{Port}:22 {ImageTag}");

        try
        {
            WaitForBanner();

            // Generated inside the running container, as tests/Ntilde.Platform.Tests/Ssh/DockerSshFixture.cs
            // does, so no private key ever sits in the image.
            Docker(
                $"exec {containerName} sh -c \"ssh-keygen -q -t ed25519 -N '' -C dev@edge-01 -f /tmp/login " +
                "&& cp /tmp/login.pub /home/dev/.ssh/authorized_keys " +
                "&& chown dev:dev /home/dev/.ssh/authorized_keys && chmod 600 /home/dev/.ssh/authorized_keys\"");

            string sshDirectory = Path.Combine(world.HomeRoot, ".ssh");
            Directory.CreateDirectory(sshDirectory);
            string keyPath = Path.Combine(sshDirectory, "edge-01_ed25519");
            Docker($"cp {containerName}:/tmp/login {keyPath}");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var profile = new TerminalProfile
            {
                Id = ProfileId,
                Name = "edge-01",
                Type = ConnectionType.SSH,
                SshHost = HostName,
                SshPort = Port,
                SshUser = UserName,
                SshBackendKind = SshBackendKind.Native,
                UseSshAgent = false,
                IdentityFilePath = keyPath,
                DefaultRemoteDir = RemoteHome,
                Group = "Production"
            };

            // SshSessionFactory reads the profile back from the store by id, so it has to be saved,
            // and AppPaths puts that store under the run's NTILDE_APPDATA_ROOT.
            new SshConnectionService().SaveConnectionProfile(profile);

            return new DemoSshHost(containerName, profile);
        }
        catch
        {
            Docker($"stop {containerName}", throwOnError: false);
            throw;
        }
    }

    public void Dispose() => Docker($"stop {_containerName}", throwOnError: false);

    private static void EnsureImage()
    {
        if (Docker($"image inspect {ImageTag}", throwOnError: false).ExitCode == 0)
        {
            return;
        }

        string context = Path.Combine(AppContext.BaseDirectory, "Assets", "demo-ssh");
        Docker($"build -q -t {ImageTag} {context}");
    }

    /// <summary>Waits until sshd answers with its identification line, not merely until the port opens.</summary>
    private static void WaitForBanner()
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("127.0.0.1", Port);
                client.ReceiveTimeout = 2000;
                var buffer = new byte[4];
                int read = client.GetStream().Read(buffer, 0, buffer.Length);
                if (read == 4 && Encoding.ASCII.GetString(buffer) == "SSH-")
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                // Not listening yet, or Docker's proxy accepted before sshd was up.
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException($"The demo SSH server never answered on 127.0.0.1:{Port}.");
    }

    private static (int ExitCode, string Output) Docker(string arguments, bool throwOnError = true)
    {
        var startInfo = new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (s_dockerHome is not null)
        {
            startInfo.Environment["HOME"] = s_dockerHome;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start docker.");
        }
        catch (System.ComponentModel.Win32Exception) when (!throwOnError)
        {
            return (-1, string.Empty);
        }

        using (process)
        {
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (throwOnError && process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"docker {arguments} failed with {process.ExitCode}: {stderr.Result.Trim()}");
            }

            return (process.ExitCode, stdout);
        }
    }
}
