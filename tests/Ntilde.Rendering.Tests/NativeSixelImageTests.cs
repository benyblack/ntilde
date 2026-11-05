using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering.Tests;

public sealed class NativeSixelImageTests
{
    public static TheoryData<string> LegacyCases => new()
    {
        "q~", "q#2~#3@", "q#2;2;100;0;0#2!5~$#3!2@-#4!3B",
        "q#1;1;120;50;100#1~", "q#1;2;200;50;0#1~",
        "q#4095;2;100;0;100#4095~", "q#4096~", "q#9~#9;2;100;0;0",
        "q#2~#2;2;0;100;0", "q!214748364799999999999~", "q!$~", "q!0~@",
        "q#1;;2;3;4~", "q#1;2;3;4;5;6~", "q#2147483648~",
        "q\"1;1;16;16#2!16~-!16~-!16N", "q#2~$#3@-#4B",
    };

    /// <summary>
    /// Compares every real decoded pixel against the legacy oracle, including late palette definitions and malformed commands.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyCases))]
    public void Fixed_storage_decoder_preserves_existing_pixels_and_register_semantics(string data)
    {
        using var expected = new SixelDecoder().Decode(data);
        Assert.NotNull(expected);
        var budget = new NativeSixelBudget();
        var (width, height) = SixelGeometry.Measure(data);
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(width, height, data.Length));
        var source = new SixelSourceBudget().Retain(data);
        using (var image = Assert.IsType<NativeSixelImage>(((INativeSixelDecoder)new SkiaImageDecoder()).DecodeNativeSixel(source, allocation)))
        {
            Assert.Equal((1, width * (long)height * 6 + 16384), budget.Usage);
            int work = 1;
            var actual = image.GetBitmap(ref work, out bool pending);
            Assert.False(pending);
            Assert.NotNull(actual);
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.Equal(width * sizeof(uint), actual.RowBytes);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            Assert.Equal((1, width * (long)height * 4), budget.Usage);
        }
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Deferred_work_holds_scratch_until_decode_and_pixels_until_actual_disposal()
    {
        const string data = "q#2!16~-!16~-!16N";
        var budget = new NativeSixelBudget();
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(16, 18, data.Length));
        var source = new SixelSourceBudget().Retain(data);
        var image = Assert.IsType<NativeSixelImage>(((INativeSixelDecoder)new SkiaImageDecoder()).DecodeNativeSixel(source, allocation));
        int work = 0;
        Assert.Null(image.GetBitmap(ref work, out bool pending));
        Assert.True(pending);
        Assert.True(image.NeedsSource);
        Assert.Equal((1, 16L * 18 * 6 + 16384), budget.Usage);
        work = 1;
        var bitmap = image.GetBitmap(ref work, out pending);
        Assert.NotNull(bitmap);
        Assert.False(pending);
        Assert.False(image.NeedsSource);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(15, 15));
        Assert.Equal(0, bitmap.GetPixel(15, 16).Alpha);
        Assert.Equal((1, 16L * 18 * 4), budget.Usage);
        Assert.Same(bitmap, image.GetBitmap(ref work, out _));
        Assert.Equal(0, work);
        image.Dispose();
        image.Dispose();
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Null(image.GetBitmap(ref work, out _));
    }

    /// <summary>
    /// Holds a real red bitmap across RIS and refused readmission; releasing the snapshot permits drain but not early credit.
    /// </summary>
    [Fact]
    public void Retired_snapshot_keeps_real_pixels_and_charge_until_it_can_be_disposed()
    {
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };
        parser.Process("\x1b[?80h\x1bPq#2~\x1b\\");
        var image = Assert.IsType<NativeSixelImage>(Assert.Single(buffer.Images).ImageHandle);
        int work = 1;
        var bitmap = image.GetBitmap(ref work, out _);
        Assert.NotNull(bitmap);
        int snapshot = buffer.BeginSnapshotSession();
        parser.Process("\u001bc");
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
        Assert.Equal((1, 24L), budget.Usage);
        parser.Process("\x1bPq~\x1b\\");
        Assert.Empty(buffer.Images);
        buffer.EndSnapshotSession(snapshot);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(image, Assert.Single(retired));
        Assert.Equal((1, 24L), budget.Usage); // Drain is not disposal.
        image.Dispose();
        Assert.Equal((0, 0L), budget.Usage);
        parser.Process("\x1bPq~\x1b\\");
        Assert.Single(buffer.Images);
        buffer.Clear(true);
        retired.Clear();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        Assert.Equal((0, 0L), budget.Usage);
    }

    /// <summary>
    /// Blocks real decoding at accounting publication and independently runs source pruning to expose lock-order regressions.
    /// The failed-input branch fills the cap and pins the first disposal frame, proving later attempts recover without another eviction.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Source_pruning_does_not_wait_for_native_decode_and_retries_a_failed_inflight_owner(bool hasData)
    {
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 8, budget, new SixelSourceBudget(100, 1));
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };
        parser.Process("\x1b[?80h\x1bPq#2~\x1b\\");
        var image = Assert.IsType<NativeSixelImage>(Assert.Single(buffer.Images).ImageHandle);
        if (!hasData) image.Source.Dispose();
        object accountingGate = typeof(NativeSixelBudget).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(budget)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = Task.Factory.StartNew(() =>
        {
            lock (accountingGate)
            {
                held.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            }
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<SKBitmap?>? decode = null;
        Task<bool>? prune = null;
        SixelSource? replacement = null;
        bool finishedBeforeRelease = false;
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            decode = Task.Factory.StartNew(() =>
            {
                int work = 1;
                return image.GetBitmap(ref work, out _);
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(SpinWait.SpinUntil(() => DecodeStarted(image), TimeSpan.FromSeconds(10)));
            replacement = buffer.SixelSources.Retain("q@");
            Assert.Null(image.Source.Data);
            prune = Task.Factory.StartNew(() => buffer.PlaceSixelImage(null, 1, 1, replacement),
                TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            finishedBeforeRelease = await Task.WhenAny(prune, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken)) == prune;
        }
        finally
        {
            release.Set();
            await blocker;
            if (decode is not null) await decode;
            if (prune is not null) await prune;
        }
        Assert.True(finishedBeforeRelease, "Pruning must not wait for the native decode lock while holding the VT write lock.");
        Assert.False(image.NeedsSource);
        Assert.Equal(hasData, image.IsMaterialized);
        if (hasData) Assert.Equal(SKColors.Red, (await decode!)!.GetPixel(0, 0));
        // The placement cap is full: cleanup must happen before another admission,
        // without another source eviction. The failed owner still owes disposal credit.
        long evictions = buffer.SixelSources.EvictionVersion;
        int invalidations = 0;
        buffer.OnInvalidate += () => Interlocked.Increment(ref invalidations);
        int pinned = buffer.BeginSnapshotSession();
        parser.Process("\x1bPq#2~\x1b\\");
        Assert.Equal(evictions, buffer.SixelSources.EvictionVersion);
        Assert.Equal((1, 24L), budget.Usage);
        if (hasData)
        {
            Assert.Same(image, Assert.Single(buffer.Images).ImageHandle);
            buffer.EndSnapshotSession(pinned);
        }
        else
        {
            Assert.Empty(buffer.Images);
            Assert.True(invalidations > 0, "Removing the failed owner must schedule its real disposal drainer.");
            var failed = new List<object>();
            buffer.DrainRetiredImageHandles(failed, long.MaxValue);
            Assert.Empty(failed); // An older snapshot can block the first requested disposal frame.
            int beforeRetry = invalidations;
            buffer.EndSnapshotSession(pinned);
            parser.Process("\x1bPq#2~\x1b\\");
            Assert.True(invalidations > beforeRetry, "A still-queued owner needs another disposal frame after unpinning.");
            Assert.Equal(evictions, buffer.SixelSources.EvictionVersion);
            buffer.DrainRetiredImageHandles(failed, long.MaxValue);
            Assert.Same(image, Assert.Single(failed));
            Assert.Equal((1, 24L), budget.Usage); // Invalidation and drain are not disposal.
            image.Dispose();
            Assert.Equal((0, 0L), budget.Usage);
            parser.Process("\x1bPq#2~\x1b\\");
            var recovered = Assert.IsType<NativeSixelImage>(Assert.Single(buffer.Images).ImageHandle);
            Assert.NotSame(image, recovered);
            int work = 1;
            Assert.Equal(SKColors.Red, recovered.GetBitmap(ref work, out _)!.GetPixel(0, 0));
        }
        buffer.ClearImages();
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        Assert.Equal((0, 0L), budget.Usage);
    }

    /// <summary>
    /// Detects the in-flight point in both the current state machine and the preserved old-source regression implementation.
    /// </summary>
    private static bool DecodeStarted(NativeSixelImage image)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        // The old implementation uses _attempted; the fixed state machine publishes Decoding=1.
        var state = typeof(NativeSixelImage).GetField("_state", flags);
        if (state is not null) return (int)state.GetValue(image)! == 1;
        return (bool)typeof(NativeSixelImage).GetField("_attempted", flags)!.GetValue(image)!;
    }

}
