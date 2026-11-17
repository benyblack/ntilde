namespace Ntilde.VT
{
    /// <summary>Lets buffer retention retire an expired source without discarding materialized pixels.</summary>
    public interface IDeferredSixelImage
    {
        bool IsMaterialized { get; }
        /// <summary>False once decoding has captured its input; reads must not wait for decoding.</summary>
        bool NeedsSource { get; }
        SixelSource Source { get; }
    }

    /// <summary>Host contract for the separately budgeted native implementation; no unbounded decoder fallback.</summary>
    internal interface INativeSixelDecoder
    {
        /// <summary>Must use only the allocation's fixed pixel/scratch limits; null owns no native resources.</summary>
        INativeSixelImage? DecodeNativeSixel(SixelSource source, NativeSixelAllocation allocation);
    }

    internal enum NativeSixelDecodeState { Pending, Decoding, Materialized, Failed, Disposed }

    /// <summary>The handle owns its reservation until successful actual disposal, including deferred and pinned time.</summary>
    internal interface INativeSixelImage : IDeferredSixelImage, IDisposable
    {
        NativeSixelAllocation Allocation { get; }
        /// <summary>One lock-free observation; VT never waits on native decoding while holding its write lock.</summary>
        NativeSixelDecodeState DecodeState { get; }
    }

    public interface IImageDecoder
    {
        object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight);
        object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight);
        object? DecodeSixel(SixelSource source, out int pixelWidth, out int pixelHeight)
        {
            if (source.DecodeData is { } data) return DecodeSixel(data, out pixelWidth, out pixelHeight);
            pixelWidth = pixelHeight = 0;
            return null;
        }


        /// <summary>
        /// Decodes raw pixel data (kitty graphics <c>f=24</c> RGB / <c>f=32</c> RGBA, e.g. the
        /// contents of a <c>t=f</c> transport file) into the same handle type the container
        /// decoder produces. <paramref name="data"/> must contain exactly
        /// <c>width * height * bytesPerPixel</c> bytes, top-down rows in order.
        /// Returns null on any dimension/length mismatch rather than throwing.
        /// </summary>
        object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight);

        /// <summary>
        /// Reads a handle this decoder produced back as top-down straight-alpha RGBA (<c>width * height * 4</c> bytes). Kitty animation
        /// needs the pixels of a frame it did not retain (the root frame, or a PNG frame) to compose later frames over them. A decoder
        /// that cannot expose pixels, or a handle that has been retired, returns false and the frame command is refused rather than guessed.
        /// </summary>
        bool TryReadPixels(object handle, out byte[] rgba, out int pixelWidth, out int pixelHeight)
        {
            rgba = [];
            pixelWidth = pixelHeight = 0;
            return false;
        }
    }
}
