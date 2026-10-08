using System.Diagnostics;
using System.Net.Sockets;

namespace Ntilde.Platform.Tests.Ssh;

/// <summary>Which of the per-container test keys to use. Both are generated when the fixture starts.</summary>
internal enum NativeSshTestKey
{
    /// <summary>Unencrypted; authenticates without any passphrase prompt.</summary>
    Plain = 0,

    /// <summary>Encrypted with <see cref="DockerSshFixture.PrivateKeyPassphrase"/>.</summary>
    PassphraseProtected = 1
}

internal sealed class DockerSshFixture : IAsyncDisposable
{
    // v3 turned on public-key and keyboard-interactive auth, which v2 refused outright.
    // v4 moves sshd host-key generation out of the image build (docker:S6437): the keys are
    // now created by the entrypoint at container start, so every run gets fresh ones.
    // v5 adds procps and iproute2 (top, pgrep, pkill, ip) for the App's remote-persistence E2E
    // (RemoteMuxDockerE2eTests), which links this file.
    // Bumping the tag matters: EnsureImageBuiltAsync reuses any already-built image with this
    // name, so a stale v3 would silently serve the new tests an image built from an older
    // Dockerfile.
    private const string ImageTag = "novaterm-native-ssh-e2e:v5";
    private const int EchoServicePortValue = 9001;

    // Needed as a constant because ProvisionTestKeysAsync is static (it runs before the fixture
    // instance exists) and has to pass the same passphrase to ssh-keygen that tests will answer with.
    private const string PrivateKeyPassphraseValue = "nova-key-pass";
    private string _containerName = string.Empty;
    private bool _started;

    private DockerSshFixture(int port)
    {
        Port = port;
    }

    public string Host => "127.0.0.1";

    /// <summary>
    /// The host port published for the container's sshd. <see cref="ReconnectNetworkAsync"/> resolves it
    /// again: a container reconnected to its network may be published on another port.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>The container's name, for <c>docker</c> commands a test runs itself.</summary>
    public string ContainerName => _containerName;

    public string UserName => "nova";
    public string Password => "nova-pass";

    /// <summary>
    /// A user the server allows ONLY via keyboard-interactive. Password and public-key auth are
    /// refused for it, so a client cannot accidentally satisfy the server by another route and leave
    /// the challenge-response path untested.
    /// </summary>
    public string KeyboardInteractiveUserName => "kbdnova";

    public string KeyboardInteractivePassword => "kbd-pass";

    /// <summary>
    /// A password user that plays the bastion in jump-chain tests, with a password different from
    /// <see cref="Password"/>. Exists only after <see cref="CreateJumpUserAsync"/>.
    /// </summary>
    /// <remarks>
    /// With one password shared by every hop, a client that sends the target's password to the
    /// bastion — or the bastion's to the target — still connects, so the credential leak jump chains
    /// are prone to stays invisible. Distinct passwords make any cross-hop reuse fail authentication.
    /// </remarks>
    public string JumpUserName => "jumpnova";

    public string JumpPassword => "jump-pass";

    /// <summary>Passphrase for <see cref="NativeSshTestKey.PassphraseProtected"/>.</summary>
    public string PrivateKeyPassphrase => PrivateKeyPassphraseValue;

    /// <summary>
    /// Port of the in-container TCP echo service, as seen from the SSH server itself. Forward tests
    /// use it as the destination so they can assert bytes complete the round trip.
    /// </summary>
    public int EchoServicePort => EchoServicePortValue;

    /// <summary>
    /// Port the in-container sshd listens on, as seen from the SSH server itself. Jump tests dial
    /// this as the next-hop and target address: the server resolves it against its own loopback,
    /// so the one container plays every hop of a chain — each hop is still a full SSH session over
    /// a real direct-tcpip tunnel, there just happens to be one sshd answering all of them.
    /// </summary>
    public int InContainerSshPort => 22;

    public async Task WriteTextFileAsync(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, contents).ConfigureAwait(false);
            await RunDockerCommandAsync($"cp \"{tempFile}\" {_containerName}:{path}")
                .ConfigureAwait(false);

            // `docker cp` writes the file as root and carries the host file's mode across, and
            // Path.GetTempFileName() creates 0600 on Linux. The result was a remote file the SSH user
            // could not read, so every SFTP download test failed with "Permission denied" — on Linux
            // only, which is why it went unnoticed until this suite first ran in CI.
            //
            // Fixed here rather than per-test: every caller writes a file it intends the session user
            // to be able to fetch, and 0644 owned by that user is what such a file looks like.
            await RunDockerCommandAsync(
                $"exec {_containerName} sh -c \"chown {UserName}:{UserName} '{path}' && chmod 644 '{path}'\"")
                .ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    public async Task<SshHostKeyInfo> GetHostKeyAsync()
    {
        string publicKey = await RunDockerCommandAsync(
            $"exec {_containerName} cat /etc/ssh/ssh_host_ed25519_key.pub")
            .ConfigureAwait(false);
        string fingerprintOutput = await RunDockerCommandAsync(
            $"exec {_containerName} ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256")
            .ConfigureAwait(false);

        string algorithm = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string fingerprint = fingerprintOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        return new SshHostKeyInfo(algorithm, fingerprint);
    }

    public async Task<string> ReadTextFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return await RunDockerCommandAsync($"exec {_containerName} cat {path}")
            .ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await RunDockerCommandAsync($"exec {_containerName} mkdir -p {path}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates <see cref="JumpUserName"/> with <see cref="JumpPassword"/> in the running container.
    /// Done at runtime rather than in the Dockerfile so the image (and its cached tag) is unchanged;
    /// the account lives and dies with this one <c>--rm</c> container.
    /// </summary>
    public async Task CreateJumpUserAsync()
    {
        string output = await RunDockerCommandAsync(
            $"exec {_containerName} sh -c \"useradd -m -s /bin/bash {JumpUserName} && echo '{JumpUserName}:{JumpPassword}' | chpasswd && echo jump-user-ready\"")
            .ConfigureAwait(false);

        if (!output.Contains("jump-user-ready", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Failed to create the jump user in container '{_containerName}'. Output: {output}");
        }
    }

    public async Task SetLoginShellAsync(string shellPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellPath);

        await RunDockerCommandAsync($"exec {_containerName} usermod -s {shellPath} {UserName}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies one of the container's private keys to <paramref name="destinationPath"/> on the host, so
    /// a test can point <c>IdentityFilePath</c> at it. The keys are generated per container by
    /// <see cref="ProvisionTestKeysAsync"/> — never committed, and never stored in the image. See the
    /// Dockerfile for why.
    /// </summary>
    public async Task<string> CopyPrivateKeyAsync(NativeSshTestKey key, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string remotePath = key switch
        {
            NativeSshTestKey.Plain => "/novaterm-keys/id_ed25519",
            NativeSshTestKey.PassphraseProtected => "/novaterm-keys/id_ed25519_encrypted",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown test key.")
        };

        await RunDockerCommandAsync($"cp {_containerName}:{remotePath} \"{destinationPath}\"")
            .ConfigureAwait(false);
        return destinationPath;
    }

    /// <summary>
    /// Runs <paramref name="shellCommand"/> in the container as root, through <c>sh -c</c>, and says how it
    /// ended. The command's own failure is not an exception: <c>pgrep</c> and <c>pkill</c> answer "nothing
    /// matched" with exit 1, which a test asserts on. Only a <c>docker exec</c> that does not finish within
    /// a minute throws.
    /// </summary>
    public async Task<DockerExecResult> ExecAsync(string shellCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellCommand);

        (int exitCode, string stdout, string stderr) = await RunDockerAsync(["exec", _containerName, "sh", "-c", shellCommand])
            .ConfigureAwait(false);
        return new DockerExecResult(exitCode, stdout.Trim(), stderr.Trim());
    }

    /// <summary>
    /// Takes the container off its network (<c>docker network disconnect bridge</c>). Everything in it keeps
    /// running, but no packet reaches it: an established connection goes silent rather than closing (on
    /// Docker Desktop the published port even keeps accepting connections, which then hear nothing), or is
    /// cut where the port's proxy goes away with the network.
    /// </summary>
    public async Task DisconnectNetworkAsync()
    {
        await RunDockerCommandAsync($"network disconnect bridge {_containerName}").ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the container back on its network (<c>docker network connect bridge</c>), then finds the host port
    /// its sshd is published on again and waits until sshd answers there with its banner. <see cref="Port"/>
    /// is updated: a reconnected container may be published on another port.
    /// </summary>
    /// <returns>
    /// False when no published port answered within <paramref name="timeout"/> (30 s by default): the mapping
    /// was lost for good, and this container cannot be reached again.
    /// </returns>
    public async Task<bool> ReconnectNetworkAsync(TimeSpan? timeout = null)
    {
        await RunDockerCommandAsync($"network connect bridge {_containerName}").ConfigureAwait(false);

        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (await TryResolveMappedPortAsync(_containerName).ConfigureAwait(false) is int port
                && await AnswersSshAsync(port).ConfigureAwait(false))
            {
                Port = port;
                return true;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Freezes every process in the container (<c>docker pause</c>). Its connections stay open and go silent:
    /// the kernel still holds them, but nothing in the container reads or writes.
    /// </summary>
    public async Task PauseAsync()
    {
        await RunDockerCommandAsync($"pause {_containerName}").ConfigureAwait(false);
    }

    /// <summary>Thaws what <see cref="PauseAsync"/> froze; the connections it held pick up where they were.</summary>
    public async Task UnpauseAsync()
    {
        await RunDockerCommandAsync($"unpause {_containerName}").ConfigureAwait(false);
    }

    public static async Task<DockerSshFixture> StartAsync()
    {
        await EnsureDockerAvailableAsync().ConfigureAwait(false);
        await EnsureImageBuiltAsync().ConfigureAwait(false);

        string containerName = $"novaterm-native-ssh-e2e-{Guid.NewGuid():N}";
        string runOutput = await RunDockerCommandAsync(
            $"run -d --rm --name {containerName} -p 127.0.0.1::22 {ImageTag}")
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(runOutput))
        {
            throw new InvalidOperationException("docker run did not return a container id.");
        }

        int mappedPort = await ResolveMappedPortAsync(containerName).ConfigureAwait(false);
        await WaitForPortAsync(containerName, mappedPort).ConfigureAwait(false);
        await WaitForEchoServiceAsync(containerName).ConfigureAwait(false);
        await ProvisionTestKeysAsync(containerName).ConfigureAwait(false);

        var fixture = new DockerSshFixture(mappedPort)
        {
            _started = true,
            _containerName = containerName
        };

        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started || string.IsNullOrWhiteSpace(_containerName))
        {
            return;
        }

        try
        {
            await RunDockerCommandAsync($"rm -f {_containerName}", throwOnFailure: false).ConfigureAwait(false);
        }
        finally
        {
            _started = false;
        }
    }

    private static async Task EnsureDockerAvailableAsync()
    {
        await RunDockerCommandAsync("info --format \"{{.ServerVersion}}\"").ConfigureAwait(false);
    }

    private static async Task EnsureImageBuiltAsync()
    {
        string rebuild = Environment.GetEnvironmentVariable("NTILDE_REBUILD_DOCKER_E2E") ?? string.Empty;
        bool shouldRebuild = rebuild == "1" || string.Equals(rebuild, "true", StringComparison.OrdinalIgnoreCase);
        if (!shouldRebuild)
        {
            string inspect = await RunDockerCommandAsync($"image inspect {ImageTag}", throwOnFailure: false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(inspect))
            {
                return;
            }
        }

        string dockerfilePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Ntilde.ExternalSuites", "NativeSsh", "Dockerfile"));
        string contextDir = Path.GetDirectoryName(dockerfilePath)
            ?? throw new InvalidOperationException("Unable to resolve Docker build context.");

        await RunDockerCommandAsync($"build -t {ImageTag} -f \"{dockerfilePath}\" \"{contextDir}\"").ConfigureAwait(false);
    }

    private static async Task<int> ResolveMappedPortAsync(string containerName)
    {
        string portOutput = await RunDockerCommandAsync($"port {containerName} 22/tcp").ConfigureAwait(false);
        string lastSegment = portOutput.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries)[^1];
        if (!int.TryParse(lastSegment, out int port))
        {
            throw new InvalidOperationException($"Unable to parse mapped SSH port from docker output '{portOutput}'.");
        }

        return port;
    }

    /// <summary>The published port of the container's sshd, or null when <c>docker port</c> names none.</summary>
    private static async Task<int?> TryResolveMappedPortAsync(string containerName)
    {
        // First line only: a port published on two addresses is listed once per address.
        string output = await RunDockerCommandAsync($"port {containerName} 22/tcp", throwOnFailure: false).ConfigureAwait(false);
        string? first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is not null && int.TryParse(first.Split(':')[^1], out int port) ? port : null;
    }

    /// <summary>
    /// Whether an SSH server greets on <paramref name="port"/> within a few seconds. A connect alone proves
    /// nothing: Docker Desktop's port proxy accepts connections for a container it cannot reach.
    /// </summary>
    private static async Task<bool> AnswersSshAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            byte[] banner = new byte[4];
            int read = 0;
            while (read < banner.Length)
            {
                int n = await stream.ReadAsync(banner.AsMemory(read), cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    return false;
                }

                read += n;
            }

            return banner is [(byte)'S', (byte)'S', (byte)'H', (byte)'-'];
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task WaitForPortAsync(string containerName, int port)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            string status = await RunDockerCommandAsync(
                $"inspect {containerName} --format \"{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}|{{{{.State.Error}}}}\"",
                throwOnFailure: false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status) && !status.StartsWith("running|", StringComparison.Ordinal))
            {
                string logs = await RunDockerCommandAsync($"logs {containerName}", throwOnFailure: false).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Docker SSH container '{containerName}' stopped before becoming ready. State: {status}. Logs: {logs}");
            }

            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
                if (client.Connected)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        string finalStatus = await RunDockerCommandAsync(
            $"inspect {containerName} --format \"{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}|{{{{.State.Error}}}}\"",
            throwOnFailure: false).ConfigureAwait(false);
        string finalLogs = await RunDockerCommandAsync($"logs {containerName}", throwOnFailure: false).ConfigureAwait(false);
        throw new TimeoutException(
            $"Docker SSH server on port {port} did not become ready within 30 seconds. State: {finalStatus}. Logs: {finalLogs}");
    }

    /// <summary>
    /// Generates the two test keypairs inside the running container and authorizes them for
    /// <see cref="UserName"/>.
    ///
    /// Done here rather than in the Dockerfile so no private key is ever stored in the image — an
    /// image carries its contents to anyone who pulls or exports it, and a build-time key would be a
    /// secret baked in for the image's whole life. The container runs with <c>--rm</c>, so these keys
    /// exist only for the duration of one test.
    /// </summary>
    private static async Task ProvisionTestKeysAsync(string containerName)
    {
        // One exec so the keys, the authorized_keys file and its permissions cannot be half-applied.
        // sshd refuses to honour authorized_keys unless it is owned by the user and not group/world
        // writable, so the chown/chmod are load-bearing rather than tidiness.
        string script =
            "ssh-keygen -q -t ed25519 -N '' -C ntilde-plain -f /novaterm-keys/id_ed25519 && " +
            $"ssh-keygen -q -t ed25519 -N '{PrivateKeyPassphraseValue}' -C ntilde-encrypted -f /novaterm-keys/id_ed25519_encrypted && " +
            "cat /novaterm-keys/id_ed25519.pub /novaterm-keys/id_ed25519_encrypted.pub > /home/nova/.ssh/authorized_keys && " +
            "chown nova:nova /home/nova/.ssh/authorized_keys && " +
            "chmod 600 /home/nova/.ssh/authorized_keys && " +
            "echo keys-provisioned";

        string output = await RunDockerCommandAsync($"exec {containerName} sh -c \"{script}\"")
            .ConfigureAwait(false);

        if (!output.Contains("keys-provisioned", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Failed to provision test keys in container '{containerName}'. Output: {output}");
        }
    }

    /// <summary>
    /// Waits for the entrypoint's echo service to accept connections. Checked at fixture start rather
    /// than inside the forwarding tests so a broken echo service reports itself as such, instead of
    /// surfacing later as a forwarding test that mysteriously reads no bytes back.
    /// </summary>
    private static async Task WaitForEchoServiceAsync(string containerName)
    {
        // socat exits non-zero when it cannot connect, so the marker only reaches stdout on success —
        // RunDockerCommandAsync collapses any failure to an empty string, which alone is ambiguous.
        string probe = $"exec {containerName} sh -c \"socat -u OPEN:/dev/null TCP:127.0.0.1:{EchoServicePortValue} && echo echo-service-ready\"";

        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            string output = await RunDockerCommandAsync(probe, throwOnFailure: false).ConfigureAwait(false);
            if (output.Contains("echo-service-ready", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        string logs = await RunDockerCommandAsync($"logs {containerName}", throwOnFailure: false).ConfigureAwait(false);
        throw new TimeoutException(
            $"Echo service on container port {EchoServicePortValue} did not accept connections within 20 seconds. Logs: {logs}");
    }

    private static async Task<string> RunDockerCommandAsync(string arguments, bool throwOnFailure = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments} failed with exit code {process.ExitCode}: {stderr}{stdout}");
        }

        if (!throwOnFailure && process.ExitCode != 0)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
    }

    /// <summary>
    /// <c>docker</c> with an argument list, so a shell command passes through as one argument whatever its
    /// quotes. Bounded: a <c>docker</c> that has not finished within a minute is killed and reported.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"docker {string.Join(' ', arguments)} did not finish within a minute.");
            }
        }

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}

internal sealed record SshHostKeyInfo(string Algorithm, string Fingerprint);

/// <summary>How a <see cref="DockerSshFixture.ExecAsync"/> command ended, its output trimmed.</summary>
internal sealed record DockerExecResult(int ExitCode, string Stdout, string Stderr);
