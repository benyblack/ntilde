using Xunit;

namespace Ntilde.Tests.Infra
{
    /// <summary>
    /// The frame-time benchmarks (#127) run alone: beside a stress test (a data flood, the memory-leak soak) a frame
    /// measures the scheduler, not the draw path. All three frame-time budgets read about 32 ms at p95 that way, against
    /// 1 to 5 ms run alone.
    /// </summary>
    [CollectionDefinition("RenderBenchmarks", DisableParallelization = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "An xUnit collection definition, named for the collection it defines.")]
    public sealed class RenderBenchmarkCollection
    {
    }
}
