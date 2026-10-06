using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Ssh.OpenSsh;

public interface IOpenSshConfigCompiler
{
    OpenSshCompilationResult Compile(IReadOnlyList<SshProfile> profiles, Guid launchProfileId);

    /// <summary>
    /// The options of <paramref name="profile"/>'s <c>Host</c> block, one config line each (no <c>Host</c> line,
    /// no indent): what <see cref="Compile"/> writes for it, and what <c>ssh -o</c> takes one at a time.
    /// </summary>
    IReadOnlyList<string> BuildHostOptions(SshProfile profile);
}

public sealed class OpenSshCompilationResult
{
    public required string ConfigFilePath { get; init; }
    public required string Alias { get; init; }
}

public sealed class OpenSshCompilerOptions
{
    public bool IsolateKnownHosts { get; init; }
    public string? KnownHostsFilePath { get; init; }
}
