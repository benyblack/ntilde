using System.Linq;
using Avalonia.Headless.XUnit;
using Ntilde.Rendering;
using Ntilde.Tests.Performance.Infra;
using Xunit;

namespace Ntilde.Tests.Performance
{
    [Collection("RendererStatistics")]
    public class RenderPerf_Backend_Offscreen
    {
        // DrawTerminalInternal called directly (capture/export/tests) never sees an Avalonia
        // lease, so every frame it writes must say Offscreen rather than claiming GPU or Software.
        [AvaloniaFact]
        public void DirectDraw_FramesAreLabelledOffscreen()
        {
            using RenderPerfRunResult run = RenderPerfSteadyScrollHarness.Run(warmupFrames: 2, measuredFrames: 8);

            Assert.NotEmpty(run.Frames);
            Assert.All(run.Frames, f => Assert.Equal(RenderBackend.Offscreen, f.Backend));
        }
    }
}
