using System;
using Ntilde.Shell;

namespace Ntilde.Tests.RenderTests;

public sealed class TextBlinkAnimationTests
{
    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        internal void Advance(long milliseconds) => _milliseconds += milliseconds;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(529, true)]
    [InlineData(530, false)]
    [InlineData(1059, false)]
    [InlineData(1060, true)]
    public void Phase_IsDeterministicWithoutWallClockTime(long milliseconds, bool visible)
    {
        Assert.Equal(visible, TextBlinkAnimation.IsVisibleAt(TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal(visible, TextBlinkAnimation.IsVisibleAtMilliseconds(milliseconds));
    }

    [Fact]
    public void SyntheticMilliseconds_DoNotOverflowTheClockRepresentation()
    {
        Assert.False(TextBlinkAnimation.IsVisibleAtMilliseconds(long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBlinkAnimation.IsVisibleAtMilliseconds(-1));
    }

    [Fact]
    public void NoBlinkInk_NeverRequestsAnAnimationRedraw()
    {
        var clock = new Clock();
        var animation = new TextBlinkAnimation(clock);
        for (int tick = 0; tick < 10; tick++)
        {
            clock.Advance(530);
            Assert.False(animation.Advance());
            Assert.True(animation.ForegroundVisible);
        }
    }

    [Fact]
    public void ActivationAndRemoval_KeepOrdinaryOutputStable()
    {
        var clock = new Clock();
        var animation = new TextBlinkAnimation(clock);
        clock.Advance(9999);
        animation.ReportCells(true);
        Assert.False(animation.Advance()); // first frame stays visible
        clock.Advance(529);
        Assert.False(animation.Advance());
        clock.Advance(1);
        Assert.True(animation.Advance());
        Assert.False(animation.ForegroundVisible);
        Assert.False(animation.Advance()); // unchanged phase does not redraw
        clock.Advance(530);
        Assert.True(animation.Advance());
        Assert.True(animation.ForegroundVisible);
        animation.ReportCells(false);
        clock.Advance(530);
        Assert.False(animation.Advance());
        Assert.True(animation.ForegroundVisible);
    }

    [Fact]
    public void Resume_StartsVisibleWithoutASecondTimer()
    {
        var clock = new Clock();
        var animation = new TextBlinkAnimation(clock);
        animation.ReportCells(true);
        animation.Advance();
        clock.Advance(530);
        animation.Advance();
        Assert.False(animation.ForegroundVisible);
        animation.Restart();
        clock.Advance(10000);
        Assert.True(animation.ForegroundVisible);
        Assert.False(animation.Advance());
        clock.Advance(530);
        Assert.True(animation.Advance());
    }
}
