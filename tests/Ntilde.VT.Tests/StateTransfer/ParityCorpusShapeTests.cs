using System.Linq;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// Self-check of the instrument Task 9's parity assertion runs on.
/// </summary>
/// <remarks>
/// <c>ParityCorpus.Recorded()</c> throws when the linked <c>.rec</c> fixtures are missing, so that
/// a broken <c>Content</c> link cannot quietly shrink the corpus to its synthetic half while the
/// parity suite still reports success. This test exists so that guard is exercised rather than
/// merely present: without it the guard's own correctness would only ever be established by the
/// disaster it is supposed to prevent.
/// </remarks>
public class ParityCorpusShapeTests
{
    [Fact]
    public void Corpus_LoadsTheLinkedReplayFixturesAndTheSyntheticStreams()
    {
        (string Name, byte[] Bytes)[] recorded = ParityCorpus.Recorded().ToArray();
        (string Name, byte[] Bytes)[] synthetic = ParityCorpus.Synthetic().ToArray();
        (string Name, byte[] Bytes)[] all = ParityCorpus.All().ToArray();

        Assert.True(
            recorded.Length >= ParityCorpus.MinimumRecordedStreams,
            $"expected at least {ParityCorpus.MinimumRecordedStreams} recorded streams, got " +
            $"{recorded.Length}: [{string.Join(", ", recorded.Select(s => s.Name))}]");

        Assert.NotEmpty(synthetic);
        Assert.Equal(recorded.Length + synthetic.Length, all.Length);

        // Every stream must carry bytes; a named-but-empty entry would make its cut sweep vacuous.
        Assert.All(all, stream => Assert.NotEmpty(stream.Bytes));

        // Names are quoted in every parity failure message, so they have to be distinguishable.
        Assert.Equal(all.Length, all.Select(s => s.Name).Distinct().Count());
    }
}
