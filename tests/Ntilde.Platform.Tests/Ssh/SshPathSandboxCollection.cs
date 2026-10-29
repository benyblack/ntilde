namespace Ntilde.Platform.Tests.Ssh;

/// <summary>
/// Serializes the tests that mutate <c>NTILDE_APPDATA_ROOT</c>. The variable is process-wide, so
/// two of them running at once would each see the other's value.
/// </summary>
[CollectionDefinition(nameof(SshPathSandboxCollection), DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "An xUnit collection definition, named for the collection it defines.")]
public sealed class SshPathSandboxCollection
{
}
