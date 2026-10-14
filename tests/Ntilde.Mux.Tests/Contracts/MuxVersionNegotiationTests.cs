using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxVersionNegotiationTests
{
    [Theory]
    [InlineData(1, 3, 2, 5, 3)]
    [InlineData(1, 1, 1, 1, 1)]
    [InlineData(2, 6, 1, 4, 4)]
    public void Overlapping_ranges_pick_the_highest_common_version(int sMin, int sMax, int cMin, int cMax, int expected)
        => Assert.Equal(expected, MuxProtocol.NegotiateVersion(sMin, sMax, cMin, cMax));

    [Theory]
    [InlineData(1, 1, 2, 3)]
    [InlineData(4, 5, 1, 3)]
    [InlineData(3, 1, 1, 3)] // inverted server range
    [InlineData(1, 3, 3, 1)] // inverted client range
    public void Disjoint_or_inverted_ranges_have_no_version(int sMin, int sMax, int cMin, int cMax)
        => Assert.Null(MuxProtocol.NegotiateVersion(sMin, sMax, cMin, cMax));
}
