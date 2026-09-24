using SkiaSharp;
using Xunit;

namespace Ntilde.Rendering.Tests;

public class RenderBackendTests
{
    [Fact]
    public void NoGpuContext_IsSoftware()
    {
        Assert.Equal(RenderBackend.Software, RenderBackend.Describe((GRContext?)null));
        Assert.Equal(RenderBackend.Software, RenderBackend.Describe((GRBackend?)null));
    }

    [Theory]
    [InlineData(GRBackend.OpenGL, "GPU/OpenGL")]
    [InlineData(GRBackend.Vulkan, "GPU/Vulkan")]
    [InlineData(GRBackend.Metal, "GPU/Metal")]
    [InlineData(GRBackend.Direct3D, "GPU/Direct3D")]
    public void GpuBackend_IsLabelledWithItsApi(GRBackend backend, string expected)
    {
        Assert.Equal(expected, RenderBackend.Describe(backend));
    }
}
