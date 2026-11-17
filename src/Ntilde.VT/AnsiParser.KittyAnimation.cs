using System;
using System.Globalization;

namespace Ntilde.VT;

/// <summary>
/// Kitty graphics animation: <c>a=f</c> frame data and <c>a=a</c> playback control. Frames are raw
/// RGB/RGBA only, the root frame's pixels are not retained (so <c>c=1</c>/<c>r=1</c> cannot be a base
/// or an edit target) and <c>a=c</c> composition stays unsupported; each limit has its own negative reply.
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
        if (!TryReadKittyFrameData(request, out byte[] data, out error)) return error;
        byte[] canvas = BuildKittyFrameCanvas(asset, request, data);
        if (canvas.Length == 0) return "ENOENT:Frame not found";
        return StoreKittyFrame(asset, request, canvas);
    }

    private bool TryReadKittyFrameRequest(KittyImageAsset asset, out KittyFrameRequest request, out string error)
    {
        request = default;
        if (!_kittyPendingParams.TryGetValue("f", out string? format) || format is not ("24" or "32"))
            return Refuse(out error, "ENOTSUP:Animation frames require raw f=24 or f=32 data");
        error = ValidateKittyPayloadEncoding() ?? "";
        if (error.Length != 0) return false;
        if (!TryReadKittyUInt("x", out uint x) || !TryReadKittyUInt("y", out uint y) || !TryReadKittyUInt("c", out uint baseFrame)
            || !TryReadKittyUInt("r", out uint editFrame) || !TryReadKittyUInt("Y", out uint background))
            return Refuse(out error, "EINVAL:Invalid animation frame control");
        if (!TryReadKittyGap(out int gap)) return Refuse(out error, "EINVAL:Invalid animation frame gap");
        if (!TryReadKittyCompositionMode(out bool overwrite)) return Refuse(out error, "EINVAL:Invalid composition mode");
        if (!TryReadKittyRawSize(out int width, out int height)) return Refuse(out error, "EINVAL:Invalid raw frame dimensions");
        if ((long)x + width > asset.PixelWidth || (long)y + height > asset.PixelHeight) return Refuse(out error, "EINVAL:Frame rectangle outside the image");
        request = new KittyFrameRequest(format == "24" ? 3 : 4, int.Parse(format, CultureInfo.InvariantCulture), new KittyFrameRect((int)x, (int)y, width, height),
            baseFrame, editFrame, background, gap, overwrite);
        if (request.CanvasFrame == 1) return Refuse(out error, "ENOTSUP:Root frame pixels are not retained");
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

    private bool TryReadKittyFrameData(KittyFrameRequest request, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        byte[]? payload = ReadKittyPayload();
        if (payload is null) return Refuse(out error, "EINVAL:Frame payload unavailable");
        payload = InflateKittyPayload(payload, request.Format, request.Rect.Width, request.Rect.Height);
        if (payload is null) return Refuse(out error, "EINVAL:Invalid compressed frame payload");
        if (payload.LongLength != (long)request.Rect.Width * request.Rect.Height * request.BytesPerPixel)
            return Refuse(out error, "EINVAL:Frame data does not match its dimensions");
        data = payload;
        error = "";
        return true;
    }

    /// <summary>The canvas the data is drawn onto, or an empty array when the named base frame has no retained pixels.</summary>
    private static byte[] BuildKittyFrameCanvas(KittyImageAsset asset, KittyFrameRequest request, byte[] data)
    {
        byte[] canvas;
        if (request.CanvasFrame != 0)
        {
            byte[]? basePixels = asset.Animation?.Frames[(int)request.CanvasFrame - 1].Rgba;
            if (basePixels is null) return Array.Empty<byte>();
            canvas = (byte[])basePixels.Clone();
        }
        else canvas = KittyFrameCompositor.SolidCanvas(asset.PixelWidth, asset.PixelHeight, request.Background);
        KittyFrameCompositor.Draw(canvas, asset.PixelWidth, data, request.BytesPerPixel, request.Rect, request.Overwrite);
        return canvas;
    }

    /// <summary>Decodes the composed pixels and stores them as a new frame or in place of the edited one.</summary>
    private string StoreKittyFrame(KittyImageAsset asset, KittyFrameRequest request, byte[] canvas)
    {
        int replaceIndex = request.Editing ? (int)request.Edit - 1 : -1;
        long charge = (long)asset.PixelWidth * asset.PixelHeight * 4 + canvas.Length;
        // A frame the quotas will refuse is refused before it is decoded: a hidden pane must not build a bitmap per rejected upload.
        KittyFrameStoreResult room = _buffer.CheckKittyFrameRoom(asset, replaceIndex, charge);
        if (room != KittyFrameStoreResult.Stored) return KittyFrameStatus(room);
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
        return existing?.GapMs ?? KittyAnimation.DefaultGapMs;
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
