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
    /// <summary><c>a=f</c>: adds a frame to a retained image, or edits one of its later frames.</summary>
    private string TransmitKittyFrame(KittyCommandIdentity identity, out uint id)
    {
        id = identity.Id;
        KittyImageAsset? asset = _buffer.FindKittyImage(identity.Id, identity.Number);
        if (asset is null) return "ENOENT:Image not found";
        id = asset.Id;
        if (!_kittyPendingParams.TryGetValue("f", out string? format) || format is not ("24" or "32"))
            return "ENOTSUP:Animation frames require raw f=24 or f=32 data";
        string? encodingError = ValidateKittyPayloadEncoding();
        if (encodingError is not null) return encodingError;
        if (!TryReadKittyUInt("x", out uint x) || !TryReadKittyUInt("y", out uint y) || !TryReadKittyUInt("c", out uint baseFrame)
            || !TryReadKittyUInt("r", out uint editFrame) || !TryReadKittyUInt("Y", out uint background))
            return "EINVAL:Invalid animation frame control";
        if (!TryReadKittyGap(out int gap)) return "EINVAL:Invalid animation frame gap";
        bool overwrite = false;
        if (_kittyPendingParams.TryGetValue("X", out string? composition))
        {
            if (composition is not ("0" or "1")) return "EINVAL:Invalid composition mode";
            overwrite = composition == "1";
        }
        if (!TryReadKittyRawSize(out int width, out int height)) return "EINVAL:Invalid raw frame dimensions";
        if ((long)x + width > asset.PixelWidth || (long)y + height > asset.PixelHeight) return "EINVAL:Frame rectangle outside the image";

        int frameCount = TerminalBuffer.KittyFrameCount(asset);
        uint canvasFrame = editFrame != 0 ? editFrame : baseFrame;
        if (canvasFrame == 1) return "ENOTSUP:Root frame pixels are not retained";
        if (canvasFrame > frameCount) return "ENOENT:Frame not found";

        byte[]? data = ReadKittyPayload();
        if (data is null) return "EINVAL:Frame payload unavailable";
        int bytesPerPixel = format == "24" ? 3 : 4;
        data = InflateKittyPayload(data, int.Parse(format, CultureInfo.InvariantCulture), width, height);
        if (data is null) return "EINVAL:Invalid compressed frame payload";
        if (data.LongLength != (long)width * height * bytesPerPixel) return "EINVAL:Frame data does not match its dimensions";

        byte[] canvas = canvasFrame != 0
            ? (byte[]?)asset.Animation?.Frames[(int)canvasFrame - 1].Rgba?.Clone() ?? Array.Empty<byte>()
            : KittyFrameCompositor.SolidCanvas(asset.PixelWidth, asset.PixelHeight, background);
        if (canvas.Length == 0) return "ENOENT:Frame not found";
        KittyFrameCompositor.Draw(canvas, asset.PixelWidth, data, bytesPerPixel, (int)x, (int)y, width, height, overwrite);

        object handle = DecodeOrPlaceholder((IImageDecoder decoder, out int w, out int h) =>
            decoder.DecodeRawImage(canvas, 4, asset.PixelWidth, asset.PixelHeight, out w, out h), asset.PixelWidth, asset.PixelHeight);
        bool editing = editFrame != 0;
        int gapMs = gap != 0 ? gap : editing ? asset.Animation!.Frames[(int)editFrame - 1].GapMs : KittyAnimation.DefaultGapMs;
        var frame = new KittyFrame(handle, canvas, gapMs, (long)asset.PixelWidth * asset.PixelHeight * 4 + canvas.Length);
        KittyFrameStoreResult stored = _buffer.StoreKittyFrame(asset, frame, editing ? (int)editFrame - 1 : -1);
        if (stored != KittyFrameStoreResult.Stored) _buffer.RetireUnstoredKittyHandle(handle);
        return stored switch
        {
            KittyFrameStoreResult.Stored => "OK",
            KittyFrameStoreResult.ImageGone => "ENOENT:Image not found",
            KittyFrameStoreResult.TooManyFrames => "ENOSPC:Too many animation frames",
            KittyFrameStoreResult.NoSpace => "ENOSPC:Animation frame storage exceeded",
            _ => "EINVAL:Frame could not be stored"
        };
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
