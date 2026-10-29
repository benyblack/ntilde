using Xunit;

namespace Ntilde.Tests.Infra;

[CollectionDefinition("WorkspacePolicy", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "An xUnit collection definition, named for the collection it defines.")]
public sealed class WorkspacePolicyCollection
{
}
