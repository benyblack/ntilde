namespace Ntilde.Tests.Infra;

/// <summary>Process-wide clipboard latch/framework subscriptions must not overlap sibling tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PrivateClipboardProcessScope
{
    public const string Name = "PrivateClipboardProcess";
}
