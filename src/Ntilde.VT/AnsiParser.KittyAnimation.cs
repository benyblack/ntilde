using System;
using System.Globalization;

namespace Ntilde.VT;

/// <summary>
/// Kitty graphics animation: <c>a=f</c> frame data and <c>a=a</c> playback control. Frames are raw RGB/RGBA
/// or PNG (<c>f=100</c>, decoded to pixels by a host decoder that can expose them), the root frame can be a base or an edit
/// target (its pixels are read back from its decoded handle when needed), and <c>a=c</c> composition stays unsupported;
/// each limit has its own negative reply.
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

    private static bool Refuse(out string error, string message)
    {
        error = message;
        return false;
    }

    /// <summary><c>a=f</c>: adds a frame to a retained image, or edits one of its later frames.</summary>
    private string TransmitKittyFrame(KittyCommandIdentity identity, out uint id)
    {
        id = identity.Id;
        KittyImageAsset? asset = _buffer.FindKittyImage(identity.Id, identity.Number);
        if (asset is null) return "ENOENT:Image not found";
        id = asset.Id;
        if (!TryReadKittyFrameRequest(asset, out KittyFrameRequest request, out string error)) return error;
        // A frame the quotas will refuse is refused before its payload is inflated, composed or decoded: a rejected upload costs no canvas and no bitmap.
        KittyFrameStoreResult room = _buffer.CheckKittyFrameRoom(asset, request.Editing ? (int)request.Edit - 1 : -1, KittyFrameCharge(asset, request.Edit));
        if (room != KittyFrameStoreResult.Stored) return KittyFrameStatus(room);
        if (!TryReadKittyFrameData(asset, ref request, out byte[] data, out error)) return error;
        byte[] canvas = BuildKittyFrameCanvas(asset, request, data);
        if (canvas.Length == 0) return request.CanvasFrame == 1 ? "ENOTSUP:Root frame pixels are unavailable" : "ENOENT:Frame not found";
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
        if (!TryReadKittyCompositionMode(out bool overwrite)) return Refuse(out error, "EINVAL:Invalid composition mode");
        int width = 0, height = 0;
        // A PNG frame's size comes from its own header once the payload is read; a raw frame declares it with s= and v=.
        if (format != "100")
        {
            if (!TryReadKittyRawSize(out width, out height)) return Refuse(out error, "EINVAL:Invalid raw frame dimensions");
            if ((long)x + width > asset.PixelWidth || (long)y + height > asset.PixelHeight) return Refuse(out error, "EINVAL:Frame rectangle outside the image");
        }
        request = new KittyFrameRequest(format == "24" ? 3 : 4, int.Parse(format, CultureInfo.InvariantCulture), new KittyFrameRect((int)x, (int)y, width, height),
            baseFrame, editFrame, background, gap, overwrite);
        if (request.CanvasFrame > TerminalBuffer.KittyFrameCount(asset)) return Refuse(out error, "ENOENT:Frame not found");
        error = "";
        return true;
    }

    /// <summary><c>X=</c>: 0 (the default) alpha-blends, 1 overwrites; anything else is invalid.</summary>
    private bool TryReadKittyCompositionMode(out bool overwrite)
    {
        overwrite = false;
        if (!_kittyPendingParams.TryGetValue("X", out string? composition)) return true;
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
    /// then it is decoded and read back as straight-alpha RGBA, which is what composition draws.
    /// </summary>
    private bool TryDecodeKittyContainerFrame(KittyImageAsset asset, ref KittyFrameRequest request, byte[] payload, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        if (ImageDecoder is not { } decoder) return Refuse(out error, "ENOTSUP:Container frames need an image decoder");
        if (!ImageHeaderProbe.TryGetSize(payload, out _, out int width, out int height) || width <= 0 || height <= 0)
            return Refuse(out error, "EINVAL:Invalid frame image data");
        KittyFrameRect rect = request.Rect with { Width = width, Height = height };
        if ((long)rect.X + width > asset.PixelWidth || (long)rect.Y + height > asset.PixelHeight) return Refuse(out error, "EINVAL:Frame rectangle outside the image");
        object? handle = decoder.DecodeImageBytes(payload, out _, out _);
        if (handle is null) return Refuse(out error, "EINVAL:Invalid frame image data");
        bool read = decoder.TryReadPixels(handle, out byte[] rgba, out int readWidth, out int readHeight);
        _buffer.RetireUnstoredKittyHandle(handle); // only its pixels are used; the stored frame is built from the composed canvas
        if (!read) return Refuse(out error, "ENOTSUP:This decoder cannot expose frame pixels");
        if (readWidth != width || readHeight != height || rgba.LongLength != (long)width * height * 4)
            return Refuse(out error, "EINVAL:Frame data does not match its dimensions");
        request = request with { Rect = rect };
        data = rgba;
        error = "";
        return true;
    }

    /// <summary>The canvas the data is drawn onto, or an empty array when the named base frame has no readable pixels.</summary>
    private byte[] BuildKittyFrameCanvas(KittyImageAsset asset, KittyFrameRequest request, byte[] data)
    {
        byte[] canvas;
        if (request.CanvasFrame != 0)
        {
            byte[]? basePixels = _buffer.ReadKittyFramePixels(asset, (int)request.CanvasFrame - 1, ImageDecoder);
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
        var frame = new KittyFrame(handle, canvas, gapMs, charge);
        KittyFrameStoreResult stored = _buffer.StoreKittyFrame(asset, frame, replaceIndex);
        if (stored == KittyFrameStoreResult.Rejected)
        {
            // A decoder handed back a handle the buffer cannot admit: keep the frame undecoded, as a whole image is, rather than refusing it.
            _buffer.RetireUnstoredKittyHandle(handle);
            handle = new UndecodedImage(asset.PixelWidth, asset.PixelHeight);
            stored = _buffer.StoreKittyFrame(asset, new KittyFrame(handle, canvas, gapMs, frame.Charge), replaceIndex);
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
        KittyFrameStoreResult.ImageGone => "ENOENT:Image not found",
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
        if (asset is null) return "ENOENT:Image not found";
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
