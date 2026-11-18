using System;
using System.Globalization;

namespace Ntilde.VT;

/// <summary>
/// Kitty graphics animation: <c>a=f</c> frame data, <c>a=a</c> playback control and <c>a=c</c> frame composition. Frames are raw RGB/RGBA
/// or PNG (<c>f=100</c>, decoded to pixels by a host decoder that can expose them), the root frame can be a base, an edit
/// target or a composition source or destination (its pixels are read back from its decoded handle when needed); each limit has its own negative reply.
/// </summary>
public partial class AnsiParser
{
    /// <summary>The validated controls of one <c>a=f</c> command.</summary>
    private readonly record struct KittyFrameRequest(int BytesPerPixel, int Format, KittyFrameRect Rect, uint Base, uint Edit, uint Background, int Gap, bool Overwrite)
    {
        internal bool Editing => Edit != 0;
        /// <summary>The frame whose pixels are the canvas: the edited one, else the base, else none.</summary>
        internal uint CanvasFrame => Edit != 0 ? Edit : Base;
    }

    /// <summary>The validated controls of one <c>a=c</c> command: 1-based frame numbers, the source rectangle and the destination offset it lands on.</summary>
    private readonly record struct KittyComposeRequest(int Source, int Destination, KittyFrameRect SourceRect, int DestX, int DestY, bool Overwrite);

    private const string ImageNotFoundStatus = "ENOENT:Image not found";

    private static bool Refuse(out string error, string message)
    {
        error = message;
        return false;
    }

    /// <summary>
    /// <c>a=c</c>: composes a rectangle of frame <c>r</c> onto frame <c>c</c> of a retained image (alpha-blended, or replacing with <c>C=1</c>); the result replaces
    /// the destination frame in place and keeps its gap. An image or frame that does not exist is <c>ENOENT</c>; a rectangle outside the image, or overlapping
    /// itself when the frames are the same, is <c>EINVAL</c>; a result the quotas cannot hold is <c>ENOSPC</c>, decided before any pixel is read.
    /// </summary>
    private string ComposeKittyFrames(KittyCommandIdentity identity, out uint id)
    {
        id = identity.Id;
        KittyImageAsset? asset = _buffer.FindKittyImage(identity.Id, identity.Number);
        if (asset is null) return ImageNotFoundStatus;
        id = asset.Id;
        if (!TryReadKittyComposeRequest(asset, out KittyComposeRequest request, out string error)) return error;
        int destinationIndex = request.Destination - 1;
        long charge = KittyFrameCharge(asset, (uint)request.Destination);
        KittyFrameStoreResult room = _buffer.CheckKittyFrameRoom(asset, destinationIndex, charge);
        if (room != KittyFrameStoreResult.Stored) return KittyFrameStatus(room);
        byte[]? canvas = _buffer.ReadKittyFramePixels(asset, destinationIndex, ImageDecoder, PersistentFrames);
        // One frame onto itself reads its pixels once: the rectangles are disjoint, so no write can change a pixel that is still to be read.
        byte[]? source = request.Source == request.Destination ? canvas : _buffer.ReadKittyFramePixels(asset, request.Source - 1, ImageDecoder, PersistentFrames);
        if (source is null || canvas is null) return "ENOTSUP:Frame pixels are unavailable";
        KittyFrameCompositor.DrawFrameRect(canvas, source, asset.PixelWidth, request.SourceRect, request.DestX, request.DestY, request.Overwrite);
        return StoreKittyFrame(asset, new KittyFrameRequest(4, 32, default, 0, (uint)request.Destination, 0, 0, request.Overwrite), canvas);
    }

    private bool TryReadKittyComposeRequest(KittyImageAsset asset, out KittyComposeRequest request, out string error)
    {
        request = default;
        if (!TryReadKittyUInt("c", out uint destination) || !TryReadKittyUInt("r", out uint source) || !TryReadKittyUInt("x", out uint x) || !TryReadKittyUInt("y", out uint y)
            || !TryReadKittyUInt("w", out uint width) || !TryReadKittyUInt("h", out uint height) || !TryReadKittyUInt("X", out uint sourceX) || !TryReadKittyUInt("Y", out uint sourceY))
            return Refuse(out error, "EINVAL:Invalid frame composition control");
        if (!TryReadKittyCompositionMode("C", out bool overwrite)) return Refuse(out error, "EINVAL:Invalid composition mode");
        int frames = TerminalBuffer.KittyFrameCount(asset);
        if (source == 0 || destination == 0 || source > frames || destination > frames) return Refuse(out error, "ENOENT:Frame not found");
        long rectWidth = width == 0 ? asset.PixelWidth : width, rectHeight = height == 0 ? asset.PixelHeight : height;
        if (sourceX + rectWidth > asset.PixelWidth || sourceY + rectHeight > asset.PixelHeight || x + rectWidth > asset.PixelWidth || y + rectHeight > asset.PixelHeight)
            return Refuse(out error, "EINVAL:Composition rectangle outside the image");
        if (source == destination && sourceX < x + rectWidth && x < sourceX + rectWidth && sourceY < y + rectHeight && y < sourceY + rectHeight)
            return Refuse(out error, "EINVAL:Source and destination rectangles overlap");
        request = new KittyComposeRequest((int)source, (int)destination, new KittyFrameRect((int)sourceX, (int)sourceY, (int)rectWidth, (int)rectHeight), (int)x, (int)y, overwrite);
        error = "";
        return true;
    }

    /// <summary><c>a=f</c>: adds a frame to a retained image, or edits one of its later frames.</summary>
    private string TransmitKittyFrame(KittyCommandIdentity identity, out uint id)
    {
        id = identity.Id;
        KittyImageAsset? asset = _buffer.FindKittyImage(identity.Id, identity.Number);
        if (asset is null) return ImageNotFoundStatus;
        id = asset.Id;
        if (!TryReadKittyFrameRequest(asset, out KittyFrameRequest request, out string error)) return error;
        // A frame the quotas will refuse is refused before its payload is inflated, composed or decoded: a rejected upload costs no canvas and no bitmap.
        KittyFrameStoreResult room = _buffer.CheckKittyFrameRoom(asset, request.Editing ? (int)request.Edit - 1 : -1, KittyFrameCharge(asset, request.Edit));
        if (room != KittyFrameStoreResult.Stored) return KittyFrameStatus(room);
        if (!TryReadKittyFrameData(asset, ref request, out byte[] data, out error)) return error;
        byte[] canvas = BuildKittyFrameCanvas(asset, request, data);
        if (canvas.Length == 0) return request.CanvasFrame == 1 ? "ENOTSUP:Root frame pixels are unavailable" : "ENOTSUP:Base frame pixels are unavailable";
        return StoreKittyFrame(asset, request, canvas);
    }

    private bool TryReadKittyFrameRequest(KittyImageAsset asset, out KittyFrameRequest request, out string error)
    {
        request = default;
        if (!_kittyPendingParams.TryGetValue("f", out string? format) || format is not ("24" or "32" or "100"))
            return Refuse(out error, "ENOTSUP:Animation frames require f=24, f=32 or f=100 data");
        error = ValidateKittyPayloadEncoding() ?? "";
        if (error.Length != 0) return false;
        if (!TryReadKittyUInt("x", out uint x) || !TryReadKittyUInt("y", out uint y) || !TryReadKittyUInt("c", out uint baseFrame)
            || !TryReadKittyUInt("r", out uint editFrame) || !TryReadKittyUInt("Y", out uint background))
            return Refuse(out error, "EINVAL:Invalid animation frame control");
        if (!TryReadKittyGap(out int gap)) return Refuse(out error, "EINVAL:Invalid animation frame gap");
        if (!TryReadKittyCompositionMode("X", out bool overwrite)) return Refuse(out error, "EINVAL:Invalid composition mode");
        if (!TryReadKittyFrameSize(asset, format == "100", x, y, out int width, out int height, out error)) return false;
        request = new KittyFrameRequest(format == "24" ? 3 : 4, int.Parse(format, CultureInfo.InvariantCulture), new KittyFrameRect((int)x, (int)y, width, height),
            baseFrame, editFrame, background, gap, overwrite);
        if (request.CanvasFrame > TerminalBuffer.KittyFrameCount(asset)) return Refuse(out error, "ENOENT:Frame not found");
        error = "";
        return true;
    }

    /// <summary>
    /// Where a frame lands and, for a raw frame, how big it is (<c>s=</c>, <c>v=</c>). The offsets are checked as the unsigned values they were sent as,
    /// before they become ints. A container frame's size comes from its own header once the payload is read, so it has none here.
    /// </summary>
    private bool TryReadKittyFrameSize(KittyImageAsset asset, bool container, uint x, uint y, out int width, out int height, out string error)
    {
        width = height = 0;
        error = "";
        if (x > asset.PixelWidth || y > asset.PixelHeight) return Refuse(out error, "EINVAL:Frame rectangle outside the image");
        if (container) return true;
        if (!TryReadKittyRawSize(out width, out height)) return Refuse(out error, "EINVAL:Invalid raw frame dimensions");
        return (long)x + width <= asset.PixelWidth && (long)y + height <= asset.PixelHeight || Refuse(out error, "EINVAL:Frame rectangle outside the image");
    }

    /// <summary>The composition mode key (<c>X=</c> for <c>a=f</c>, <c>C=</c> for <c>a=c</c>): 0 (the default) alpha-blends, 1 overwrites; anything else is invalid.</summary>
    private bool TryReadKittyCompositionMode(string key, out bool overwrite)
    {
        overwrite = false;
        if (!_kittyPendingParams.TryGetValue(key, out string? composition)) return true;
        overwrite = composition == "1";
        return composition is "0" or "1";
    }

    private bool TryReadKittyFrameData(KittyImageAsset asset, ref KittyFrameRequest request, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        byte[]? payload = ReadKittyPayload();
        if (payload is null) return Refuse(out error, "EINVAL:Frame payload unavailable");
        payload = InflateKittyPayload(payload, request.Format, request.Rect.Width, request.Rect.Height);
        if (payload is null) return Refuse(out error, "EINVAL:Invalid compressed frame payload");
        if (request.Format == 100) return TryDecodeKittyContainerFrame(asset, ref request, payload, out data, out error);
        if (payload.LongLength != (long)request.Rect.Width * request.Rect.Height * request.BytesPerPixel)
            return Refuse(out error, "EINVAL:Frame data does not match its dimensions");
        data = payload;
        error = "";
        return true;
    }

    /// <summary>
    /// A container (PNG) frame: its size is read from the header before anything is decoded, so a frame that cannot fit the image costs no bitmap,
    /// then it is read as straight-alpha RGBA, which is what composition draws. In an ordinary tab a host decoder reads it; where frames are
    /// retained for snapshots (a persistent session) <see cref="PngPixelReader"/> does, so a daemon with no decoder decides as its clients do.
    /// </summary>
    private bool TryDecodeKittyContainerFrame(KittyImageAsset asset, ref KittyFrameRequest request, byte[] payload, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        if (!ImageHeaderProbe.TryGetSize(payload, out ImageContainerFormat container, out int width, out int height) || width <= 0 || height <= 0)
            return Refuse(out error, "EINVAL:Invalid frame image data");
        KittyFrameRect rect = request.Rect with { Width = width, Height = height };
        if ((long)rect.X + width > asset.PixelWidth || (long)rect.Y + height > asset.PixelHeight) return Refuse(out error, "EINVAL:Frame rectangle outside the image");
        byte[] rgba;
        if (PersistentFrames)
        {
            if (container != ImageContainerFormat.Png) return Refuse(out error, "ENOTSUP:Only PNG container frames are supported where frames are retained for snapshots");
            if (!PngPixelReader.TryRead(payload, Math.Max(asset.PixelWidth, asset.PixelHeight), out int readWidth, out int readHeight, out rgba) || (readWidth, readHeight) != (width, height))
                return Refuse(out error, "EINVAL:Invalid frame image data");
        }
        else if (!TryReadDecodedKittyFrame(payload, width, height, out rgba, out error)) return false;
        request = request with { Rect = rect };
        data = rgba;
        error = "";
        return true;
    }

    /// <summary>The pixels of a container frame as the host decoder reads them back; the decoded bitmap is released at once, since only its pixels are used.</summary>
    private bool TryReadDecodedKittyFrame(byte[] payload, int width, int height, out byte[] rgba, out string error)
    {
        rgba = [];
        if (ImageDecoder is not { } decoder) return Refuse(out error, "ENOTSUP:Container frames need an image decoder");
        object? handle = decoder.DecodeImageBytes(payload, out _, out _);
        if (handle is null) return Refuse(out error, "EINVAL:Invalid frame image data");
        bool read = decoder.TryReadPixels(handle, out rgba, out int readWidth, out int readHeight);
        _buffer.ReleaseRejectedImageHandle(handle); // it was never admitted: release it now, not at a renderer drain
        if (!read) return Refuse(out error, "ENOTSUP:This decoder cannot expose frame pixels");
        if (readWidth != width || readHeight != height || rgba.LongLength != (long)width * height * 4)
            return Refuse(out error, "EINVAL:Frame data does not match its dimensions");
        error = "";
        return true;
    }

    /// <summary>The canvas the data is drawn onto, or an empty array when the named base frame has no readable pixels.</summary>
    private byte[] BuildKittyFrameCanvas(KittyImageAsset asset, KittyFrameRequest request, byte[] data)
    {
        byte[] canvas;
        if (request.CanvasFrame != 0)
        {
            byte[]? basePixels = _buffer.ReadKittyFramePixels(asset, (int)request.CanvasFrame - 1, ImageDecoder, PersistentFrames);
            if (basePixels is null) return Array.Empty<byte>();
            canvas = basePixels;
        }
        else canvas = KittyFrameCompositor.SolidCanvas(asset.PixelWidth, asset.PixelHeight, request.Background);
        KittyFrameCompositor.Draw(canvas, asset.PixelWidth, data, request.BytesPerPixel, request.Rect, request.Overwrite);
        return canvas;
    }

    /// <summary>Decodes the composed pixels and stores them as a new frame or in place of the edited one.</summary>
    private string StoreKittyFrame(KittyImageAsset asset, KittyFrameRequest request, byte[] canvas)
    {
        int replaceIndex = request.Editing ? (int)request.Edit - 1 : -1;
        long charge = KittyFrameCharge(asset, request.Edit);
        object handle = DecodeOrPlaceholder((IImageDecoder decoder, out int w, out int h) =>
            decoder.DecodeRawImage(canvas, 4, asset.PixelWidth, asset.PixelHeight, out w, out h), asset.PixelWidth, asset.PixelHeight);
        int gapMs = request.Gap;
        if (gapMs == 0) gapMs = request.Editing ? EditedKittyFrameGap(asset, request.Edit) : KittyAnimation.DefaultGapMs;
        // Where frames are retained for snapshots, the frame's pixels live only as its packed retained source (evictable with the rest of the budget).
        byte[]? packed = PersistentFrames ? KittyFrameCodec.Encode(canvas) : null;
        byte[]? kept = PersistentFrames ? null : canvas;
        var frame = new KittyFrame(handle, kept, gapMs, charge);
        KittyFrameStoreResult stored = _buffer.StoreKittyFrame(asset, frame, replaceIndex, packed);
        if (stored == KittyFrameStoreResult.Rejected)
        {
            // A decoder handed back a handle the buffer cannot admit: keep the frame undecoded, as a whole image is, rather than refusing it.
            _buffer.RetireUnstoredKittyHandle(handle);
            handle = new UndecodedImage(asset.PixelWidth, asset.PixelHeight);
            stored = _buffer.StoreKittyFrame(asset, new KittyFrame(handle, kept, gapMs, frame.Charge), replaceIndex, packed);
        }
        if (stored != KittyFrameStoreResult.Stored) _buffer.RetireUnstoredKittyHandle(handle);
        return KittyFrameStatus(stored);
    }

    /// <summary>What a frame of this image costs against the quota: its decoded pixels plus the RGBA kept for later composition.</summary>
    private static long KittyFrameCharge(KittyImageAsset asset, uint editedFrame) =>
        // An edited root frame's decoded pixels are the asset's own, already charged; only the RGBA kept for composition is new.
        (editedFrame == 1 ? 4L : 8L) * asset.PixelWidth * asset.PixelHeight;

    private static string KittyFrameStatus(KittyFrameStoreResult result) => result switch
    {
        KittyFrameStoreResult.Stored => "OK",
        KittyFrameStoreResult.ImageGone => ImageNotFoundStatus,
        KittyFrameStoreResult.TooManyFrames => "ENOSPC:Too many animation frames",
        KittyFrameStoreResult.NoSpace => "ENOSPC:Animation frame storage exceeded",
        _ => "EINVAL:Frame could not be stored"
    };

    private static int EditedKittyFrameGap(KittyImageAsset asset, uint frameNumber)
    {
        KittyFrame? existing = asset.Animation?.Frames[(int)frameNumber - 1];
        // The root has no gap of its own until one is set (the playhead holds it for the default).
        return existing?.GapMs ?? (frameNumber == 1 ? 0 : KittyAnimation.DefaultGapMs);
    }

    /// <summary><c>a=a</c>: sets a frame gap, the displayed frame, loop count and run state of a retained image.</summary>
    private string ControlKittyAnimation(KittyCommandIdentity identity, out uint id)
    {
        id = identity.Id;
        KittyImageAsset? asset = _buffer.FindKittyImage(identity.Id, identity.Number);
        if (asset is null) return ImageNotFoundStatus;
        id = asset.Id;
        KittyAnimationState? state = null;
        if (_kittyPendingParams.TryGetValue("s", out string? stateText))
        {
            if (stateText is not ("1" or "2" or "3")) return "EINVAL:Invalid animation state";
            state = (KittyAnimationState)int.Parse(stateText, CultureInfo.InvariantCulture);
        }
        if (!TryReadKittyUInt("v", out uint loops) || !TryReadKittyUInt("r", out uint gapFrame) || !TryReadKittyUInt("c", out uint current)
            || !TryReadKittyGap(out int gap))
            return "EINVAL:Invalid animation control";
        int frames = TerminalBuffer.KittyFrameCount(asset);
        if (gapFrame > frames || current > frames) return "ENOENT:Frame not found";
        _buffer.ControlKittyAnimation(asset, new KittyAnimationControl(state, loops, gapFrame, gap, current));
        return "OK";
    }

    /// <summary><c>z=</c>: signed milliseconds; absent and zero both read as unspecified (0).</summary>
    private bool TryReadKittyGap(out int gap)
    {
        gap = 0;
        return !_kittyPendingParams.TryGetValue("z", out string? text)
            || (text.Length > 0 && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out gap));
    }
}
