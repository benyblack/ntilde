using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Sessions;
using Ntilde.Pty;
using Ntilde.Replay;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class SshByteInputForwardingTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Wrappers_forward_capability_exact_bytes_and_refusal_without_text_fallback(bool compatibilityWrapper, int mode)
    {
        var child = new ByteSession(mode);
        var launcher = new Launcher(child);
        using ITerminalSession session = compatibilityWrapper
            ? new SshSession(Guid.NewGuid(), new Planner(), launcher: launcher, log: _ => { })
            : new OpenSshSession(Guid.NewGuid(), new Planner(), launcher: launcher, log: _ => { });
        var sink = Assert.IsAssignableFrom<ITerminalByteInput>(session);
        byte[] input = [27, 91, 77, 32, 128, 255], expected = input.ToArray();
        Assert.Equal(mode != 0, sink.SupportsByteInput);
        Assert.Equal(mode == 1, sink.TrySendInputBytes(input));
        Array.Fill(input, (byte)0);
        if (mode == 1) Assert.Equal(expected, Assert.Single(child.Bytes));
        else Assert.Empty(child.Bytes);
        Assert.Empty(child.Text);
        session.SendInput("ordinary €");
        Assert.Equal("ordinary €", Assert.Single(child.Text));
    }

    private sealed class Planner : ISshLaunchPlanner
    {
        public SshLaunchPlan Plan(Guid profileId, IReadOnlyList<string>? extraArgs = null) => new()
        {
            ProfileId = profileId,
            SshExecutablePath = "fixture-ssh",
            ConfigFilePath = "none",
            Alias = "fixture",
            Arguments = ["fixture"]
        };
    }
    private sealed class Launcher(ITerminalSession child) : ISshProcessLauncher
    {
        public ITerminalSession Launch(string executablePath, string arguments, int cols, int rows, string? cwd) => child;
    }
    private sealed class ByteSession(int mode) : ITerminalSession, ITerminalByteInput
    {
        public List<byte[]> Bytes { get; } = [];
        public List<string> Text { get; } = [];
        public bool SupportsByteInput => mode != 0;
        public bool TrySendInputBytes(ReadOnlyMemory<byte> input)
        {
            if (mode != 1) return false;
            Bytes.Add(input.ToArray()); return true;
        }
        public void SendInput(string input) => Text.Add(input);
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsProcessRunning => true;
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => null;
        public string ShellCommand => "fixture";
        public string? ShellArguments => null;
        public bool IsRecording => false;
        public bool IsFlightRecording => false;
        public event Action<string>? OnOutputReceived { add { } remove { } }
        public event Action<int>? OnExit { add { } remove { } }
        public void Resize(int cols, int rows) { }
        public void Dispose() { }
        public void StartRecording(string filePath) => throw new NotSupportedException();
        public void StopRecording() { }
        public void EnableFlightRecording(long maxTotalBytes) => throw new NotSupportedException();
        public void DisableFlightRecording() { }
        public bool TryExportFlightRecording(string filePath, out FlightExportInfo info) { info = default!; return false; }
    }
}
