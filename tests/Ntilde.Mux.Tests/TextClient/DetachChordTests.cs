using System.Text;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class DetachChordTests
{
    [Theory]
    [InlineData("abc", "abc", false)]
    [InlineData("ab\u001cd", "ab", true)]
    [InlineData("\u001cD", "", true)]
    [InlineData("ab\u001c\u0004", "ab", true)]          // Ctrl still held for the d: Ctrl+D detaches too, as in GNU screen
    [InlineData("\u0004", "\u0004", false)]              // Ctrl+D on its own is the shell's
    [InlineData("\u001c\u001c", "\u001c", false)]      // Ctrl+\ Ctrl+\ sends one literal Ctrl+\
    [InlineData("\u001c\u001c\u0004", "\u001c\u0004", false)] // the way to send a literal Ctrl+\ then Ctrl+D
    [InlineData("\u001cx", "\u001cx", false)]          // anything else: nothing is lost
    [InlineData("\u001cdls", "", true)]                // input after the chord is not sent
    public void Feed(string input, string expectedPassThrough, bool expectedDetach)
    {
        var chord = new DetachChord();
        var pass = new StringBuilder();

        bool detach = chord.Feed(input, pass);

        Assert.Equal(expectedDetach, detach);
        Assert.Equal(expectedPassThrough, pass.ToString());
    }

    [Fact]
    public void A_chord_split_across_reads_still_detaches()
    {
        var chord = new DetachChord();
        var pass = new StringBuilder();

        Assert.False(chord.Feed("x\u001c", pass));
        Assert.True(chord.Feed("d", pass));
        Assert.Equal("x", pass.ToString());
    }
}
