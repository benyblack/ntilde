namespace Ntilde.VT
{
    /// <summary>
    /// The kitty graphics protocol (APC G): transmitting, placing and deleting images.
    /// </summary>
    public partial class AnsiParser
    {
        /// <summary>
        /// Length cap on the echoed kitty graphics image id. Ids are unsigned 32-bit, so 10 digits
        /// covers every legal value.
        /// </summary>
        private const int KittyMaxEchoedIdChars = 10;
        private const int KittyMaxControlValueChars = 64;

        private static bool IsBoundedKittyControl(string key, string value) =>
            key.Length == 1 && char.IsAsciiLetter(key[0]) && value.Length <= KittyMaxControlValueChars;

        /// <summary>
        /// Inflation ceiling for <c>o=z</c> container payloads (f absent or encoded): the
        /// inflated bytes are an encoded image the decoder will bound at 2000×2000, and no
        /// legitimate encoded container of that geometry approaches 32 MiB. Raw payloads
        /// (f=24/32) bypass this constant — they are bounded by their exact declared size.
        /// </summary>
        internal const long KittyMaxInflatedContainerBytes = 32L * 1024 * 1024;

        /// <summary>
        /// Pixel-dimension guardrail for raw kitty payloads (f=24/32), enforced BEFORE
        /// inflation so the declared s/v dimensions cannot license an oversized decompression
        /// that the decoder would only reject afterwards. Mirrors SkiaImageDecoder's default
        /// <c>MaxPixelDimension</c>.
        /// </summary>
        private const int KittyMaxRawPixelDimension = 2000;

        private void HandleApc(string content)
        {
            TerminalLogger.Debug($"[ANSI_PARSER] HandleApc: {content.Substring(0, Math.Min(content.Length, 20))}...");
            // Kitty protocol uses 'G' as command identifier in APC
            if (content.StartsWith('G'))
            {
                HandleKittyGraphics(content.Substring(1), isTunneled: false);
            }
        }

        private void HandleKittyGraphics(string content, bool isTunneled)
        {
            TerminalLogger.Debug($"[ANSI_PARSER] HandleKittyGraphics: {content.Substring(0, Math.Min(content.Length, 40))}...");

            // The Kitty protocol control string starts with 'G'.
            if (content.StartsWith('G')) content = content.Substring(1);

            // Format: params ; payload
            var parts = content.Split(';', 2);
            string paramsPart = parts[0];
            string payload = parts.Length > 1 ? parts[1] : "";

            // A deletion interrupts any incomplete image upload rather than inheriting
            // its identity, payload or m=1 state.
            if (paramsPart.Split(',').Contains("a=d")) ClearKittyState();
            ReadKittyParams(paramsPart);
            AppendKittyPayload(payload);

            // Check if more chunks are coming (m=1)
            bool more = false;
            if (paramsPart.Contains("m=1")) more = true;
            else if (paramsPart.Contains("m=0")) more = false;
            else if (_kittyPendingParams.TryGetValue("m", out var mVal)) more = (mVal == "1");

            if (more)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] Kitty chunk received, waiting for more (m=1). Buffer size: {_kittyPayloadBuffer.Length}");
                return;
            }

            // Process finalized image
            try
            {
                ExecuteKittyCommand(isTunneled);
            }
            catch (Exception ex)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] Failed to decode Kitty image: {ex.Message}");
            }
            finally
            {
                // The command is complete, whatever became of it, so the next one starts clean.
                ClearKittyState();
            }
        }

        /// <summary>
        /// Adds a chunk's <c>key=value</c> control data to the pending command, a later value for a key
        /// replacing an earlier one.
        /// </summary>
        private void ReadKittyParams(string paramsPart)
        {
            var kvParams = paramsPart.Split(',');
            foreach (var kv in kvParams)
            {
                var side = kv.Split('=');
                string key = side[0];
                string value = side.Length == 2 ? side[1] : "1";
                if (kv.Length == 0) continue;
                if (side.Length > 2 || !IsBoundedKittyControl(key, value))
                {
                    // All protocol keys are one ASCII letter. This bounds the cross-chunk
                    // dictionary to 52 keys and 64 chars per value, including unknown keys.
                    _kittyPayloadOverflow = true;
                    ResetKittyPayloadBuffer();
                    // Preserve the existing bounded query echo without retaining the huge
                    // value. Non-query commands remain rejected by the overflow latch.
                    if (key == "i" && side.Length == 2)
                        _kittyPendingParams[key] = SanitizeEchoParameter(value, "0123456789", KittyMaxEchoedIdChars, "31");
                    continue;
                }
                _kittyPendingParams[key] = value;
            }
        }

        // Accumulate payload. Bounded so a hostile stream that keeps sending m=1
        // continuation chunks (and never the final m=0) cannot grow this unboundedly;
        // per-sequence APC accumulation is already capped, but the cross-chunk total
        // needs its own guard.
        private void AppendKittyPayload(string payload)
        {
            if (!_kittyPayloadOverflow && _kittyPayloadBuffer.Length + payload.Length <= MaxStringSequenceChars)
            {
                _kittyPayloadBuffer.Append(payload);
            }
            else if (!_kittyPayloadOverflow)
            {
                // Payload exceeded the cap mid-stream: abort this image. Free the buffer now
                // rather than holding up to the cap across further continuation chunks, and
                // remember to skip the (truncated, undecodable) payload at the terminator
                // instead of spending CPU base64-decoding garbage.
                _kittyPayloadOverflow = true;
                ResetKittyPayloadBuffer();
            }
        }

        /// <summary>
        /// Carries out a kitty graphics command once its last chunk has arrived: answers a query, or
        /// decodes a transmitted image and places it. Returning early drops the command;
        /// <see cref="HandleKittyGraphics"/> clears the pending state afterwards either way, and logs
        /// anything thrown here (a payload that is not base64, for one).
        /// </summary>
        private void ExecuteKittyCommand(bool isTunneled)
        {
            string action = _kittyPendingParams.TryGetValue("a", out var aVal) ? aVal : "t";
            if (action == "q")
            {
                ExecuteKittyQuery(isTunneled);
                return;
            }
            if (!TryReadKittyIdentity(out KittyCommandIdentity identity)) return;
            string status;
            uint id = identity.Id;
            if (_kittyPayloadOverflow) status = "E2BIG:Graphics payload or controls exceeded bounds";
            else if (!AllowNonSixelImages || IsNativeKittyBlocked(isTunneled)) status = "ENOTSUP:Graphics disabled by host";
            else
            {
                try
                {
                    status = action switch
                    {
                        "t" or "T" => TransmitKittyImage(identity, action == "T", out id),
                        "p" => DisplayStoredKittyImage(identity, out id),
                        "d" => DeleteStoredKittyImages(identity),
                        _ => "ENOTSUP:Graphics action not supported"
                    };
                }
                catch (Exception ex)
                {
                    TerminalLogger.Debug($"[ANSI_PARSER] Kitty command failed: {ex.Message}");
                    status = "EINVAL:Invalid image data";
                }
            }
            // Successful deletion has no acknowledgement. Errors still obey quiet policy.
            if (action != "d" || status != "OK") ReplyKittyCommand(identity with { Id = id }, status);
        }

        private void ExecuteKittyQuery(bool isTunneled)
        {
            if (_kittyPendingParams.ContainsKey("i") && _kittyPendingParams.ContainsKey("I"))
                TryReadKittyIdentity(out _);
            else AnswerKittyQuery(isTunneled);
        }

        /// <summary>
        /// Whether a kitty command that arrived as a native APC, not tunneled through OSC 1339, is refused:
        /// ConPTY is likely filtering those and the host has not opted into them
        /// (<see cref="AllowNativeKittyGraphics"/>). A refused query answers ERR and a refused image is
        /// skipped.
        /// </summary>
        private bool IsNativeKittyBlocked(bool isTunneled) =>
            _isConPtyFilteringLikely && !isTunneled && !AllowNativeKittyGraphics;

        /// <summary>
        /// <c>a=q</c>: answers a capability probe with OK or ERR and places nothing.
        /// </summary>
        private void AnswerKittyQuery(bool isTunneled)
        {
            TerminalLogger.Debug($"[ANSI_PARSER] Kitty: Handling query (a=q)");
            // If we are likely under ConPTY and this is non-tunneled Kitty APC, advertise
            // unsupported so clients can choose Sixel or other fallback.
            // SECURITY (PR #280 review): the kitty `i=` param is attacker-controlled and
            // was echoed raw, which is the same response-injection / write-amplification
            // bug as the OSC 52 denial reply above - the APC accumulator passes CR/LF
            // through, and this reply lands on the child's stdin. Image ids are numeric,
            // so whitelist digits and cap the length, falling back to the same "31"
            // default used when the param is absent.
            string id = SanitizeEchoParameter(
                _kittyPendingParams.TryGetValue("i", out var idVal) ? idVal : "31",
                "0123456789",
                KittyMaxEchoedIdChars,
                "31");
            // A capability probe names the transport it intends to use (`t=`), and the
            // reply must reflect what the transmit path will actually accept - the
            // whitelist below is exactly d and f. For f the host must also have wired
            // a file reader: without one (e.g. an SSH pane, where the path names a
            // remote file) every f-transport frame would be skipped, so the probe
            // answers ERR and the client falls back to inline payloads. Answering OK
            // to anything else (s, t, or a future value) would send the client into a
            // mode where every frame is skipped.
            string probeTransport = _kittyPendingParams.TryGetValue("t", out var probeT) ? probeT : "d";
            bool transportSupported = probeTransport == "d"
                || (probeTransport == "f" && ReadFileBytes != null);
            string status = !AllowNonSixelImages || IsNativeKittyBlocked(isTunneled) || !transportSupported
                ? "ERR"
                : "OK";
            OnResponse?.Invoke($"\x1b_Gi={id};{status}\x1b\\");
        }

        /// <summary>
        /// <c>a=t</c> retains decoded data; <c>a=T</c> also creates its initial placement.
        /// </summary>
        private string TransmitKittyImage(KittyCommandIdentity identity, bool display, out uint id)
        {
            id = identity.Id;
            string? encodingError = ValidateKittyPayloadEncoding();
            if (encodingError is not null) return encodingError;
            byte[]? data = ReadKittyPayload();
            if (data == null) return "EINVAL:Image payload unavailable";

            // Format (kitty key `f`) is parsed BEFORE decompression: 24/32 are raw RGB/RGBA
            // pixel data sized by s=/v=, and the declared dimensions give the inflate step
            // an exact bound. Anything else (including the absent default) goes through the
            // container decoder, which sniffs PNG/JPEG/etc. terminal-browser sends f=32
            // with o=z.
            int format = 0;
            if (_kittyPendingParams.TryGetValue("f", out var fVal))
            {
                _ = int.TryParse(fVal, out format); // a malformed f= reads as 0, the container-decoder default
            }

            if (!TryReadKittyDimensions(format, out int rawWidth, out int rawHeight)) return "EINVAL:Invalid raw image dimensions";

            byte[] sent = data;
            data = InflateKittyPayload(data, format, rawWidth, rawHeight);
            if (data == null) return "EINVAL:Invalid compressed image payload";

            // The size, and so every decision below, comes from the data itself (s=/v= or the
            // container header), never from the decoder: a parser without one (the persistent-session
            // daemon) must reserve the same cells and give the same reply as a window with one (#838).
            if (!TryKittyImageSize(data, format, rawWidth, rawHeight, out int pixelWidth, out int pixelHeight))
                return "EINVAL:Image decode failed";
            if (pixelWidth > KittyMaxRawPixelDimension || pixelHeight > KittyMaxRawPixelDimension)
                return "EINVAL:Decoded image dimensions exceed bounds";

            // The decoder only supplies pixels. Without it, or when the body does not decode, the image
            // still holds its cells and draws nothing there.
            object imageHandle = DecodeOrPlaceholder((IImageDecoder decoder, out int w, out int h) => DecodeKittyImage(decoder, data, format, rawWidth, rawHeight, out w, out h),
                pixelWidth, pixelHeight);
            TerminalLogger.Debug($"[ANSI_PARSER] Kitty image {pixelWidth}x{pixelHeight}, {(imageHandle is UndecodedImage ? "not decoded" : "decoded")}");

            // A decoder that hands back an already retired handle cannot change the outcome either: the
            // image is kept undecoded, as a parser without a decoder keeps it.
            // Where snapshots carry images, the bytes are kept as sent (before o=z inflation) with how
            // they were sent, so a restore decodes them exactly as this transmission did (#838).
            ImageSource? source = RetainImageSources ? _buffer.ImageSources.Retain(sent) : null;
            var encoding = source is null ? null : new KittyImageEncoding(format, _kittyPendingParams.ContainsKey("o"), rawWidth, rawHeight);
            KittyImageAsset? asset = _buffer.StoreKittyImage(imageHandle, pixelWidth, pixelHeight, identity.Id, identity.Number, source, encoding)
                ?? _buffer.StoreKittyImage(new UndecodedImage(pixelWidth, pixelHeight), pixelWidth, pixelHeight, identity.Id, identity.Number, source, encoding);
            if (asset is null)
            {
                source?.Release();
                return "EINVAL:Image could not be stored";
            }
            id = asset.Id;
            if (display) PlaceKittyImage(asset, identity.HasIdentity ? identity.Placement : 0);
            return "OK";
        }

        private string? ValidateKittyPayloadEncoding()
        {
            if (_kittyPendingParams.TryGetValue("f", out string? format) && format is not ("24" or "32" or "100"))
                return "ENOTSUP:Image format not supported";
            if (_kittyPendingParams.TryGetValue("o", out string? compression) && compression != "z")
                return "ENOTSUP:Compression not supported";
            return null;
        }

        private bool TryReadKittyDimensions(int format, out int width, out int height)
        {
            width = height = 0;
            return format is not (24 or 32) || TryReadKittyRawSize(out width, out height);
        }

        private byte[]? InflateKittyPayload(byte[] data, int format, int rawWidth, int rawHeight)
        {
            if (!_kittyPendingParams.ContainsKey("o")) return data;
            int bytesPerPixel = format == 24 ? 3 : 4;
            long expected = format is 24 or 32
                ? (long)rawWidth * rawHeight * bytesPerPixel
                : KittyMaxInflatedContainerBytes;
            return InflateZlib(data, (int)Math.Min(expected, int.MaxValue));
        }

        /// <summary>
        /// The pixel size of a transmitted image: <c>s=</c>/<c>v=</c> for raw data, which must then be
        /// exactly that many pixels, else the container header (<see cref="ImageHeaderProbe"/>).
        /// </summary>
        private static bool TryKittyImageSize(byte[] data, int format, int rawWidth, int rawHeight, out int pixelWidth, out int pixelHeight)
        {
            if (format is 24 or 32)
            {
                (pixelWidth, pixelHeight) = (rawWidth, rawHeight);
                return data.LongLength == (long)rawWidth * rawHeight * (format == 24 ? 3 : 4);
            }
            return ImageHeaderProbe.TryGetSize(data, out _, out pixelWidth, out pixelHeight);
        }

        private static object? DecodeKittyImage(IImageDecoder decoder, byte[] data, int format, int width, int height,
            out int pixelWidth, out int pixelHeight)
        {
            int bytesPerPixel = format == 24 ? 3 : 4;
            return format is 24 or 32
                ? decoder.DecodeRawImage(data, bytesPerPixel, width, height, out pixelWidth, out pixelHeight)
                : decoder.DecodeImageBytes(data, out pixelWidth, out pixelHeight);
        }

        /// <summary>
        /// The image bytes a command carries: its base64 payload decoded, or for <c>t=f</c> the contents
        /// of the file the payload names. Null, after logging why, when there is nothing to decode or
        /// no way to read it.
        /// </summary>
        private byte[]? ReadKittyPayload()
        {
            string combinedPayload = _kittyPayloadBuffer.ToString();
            if (string.IsNullOrEmpty(combinedPayload)) return null;

            // The APC/tunnel command prefix was removed before splitting controls from payload.
            // Every base64 character here is image data, including an initial 'G'.

            TerminalLogger.Debug($"[ANSI_PARSER] Kitty payload preview: {combinedPayload.Substring(0, Math.Min(combinedPayload.Length, 20))}...");
            byte[] data = Convert.FromBase64String(combinedPayload);

            // Transport (kitty key `t`): d = inline payload (default), f = file whose path
            // is the payload. Whitelist: any other value (s = shared memory, t = temporary
            // file, or something newer) must skip rather than fall through to inline
            // decoding - a t=t payload is a pathname, and decoding it as image bytes drops
            // every frame while the probe already advertised support. Reading a file is
            // host-injected I/O - the VT layer never touches disk itself (ReadFileBytes).
            string transport = _kittyPendingParams.TryGetValue("t", out var tVal) ? tVal : "d";
            if (transport != "d" && transport != "f")
            {
                TerminalLogger.Debug($"[ANSI_PARSER] Kitty transport '{transport}' not supported, skipping.");
                return null;
            }

            if (transport == "f")
            {
                var readBytes = ReadFileBytes;
                if (readBytes == null)
                {
                    TerminalLogger.Debug("[ANSI_PARSER] Kitty t=f image skipped: no ReadFileBytes delegate wired.");
                    return null;
                }

                byte[]? fileData = readBytes(System.Text.Encoding.UTF8.GetString(data));
                if (fileData == null)
                {
                    TerminalLogger.Debug("[ANSI_PARSER] Kitty t=f image skipped: ReadFileBytes returned no data.");
                    return null;
                }

                data = fileData;
            }

            return data;
        }

        /// <summary>
        /// The pixel size raw data (<c>f=24</c> / <c>f=32</c>) declares with <c>s=</c> and <c>v=</c>. False,
        /// after logging why, when either is missing or not positive, or when the size is past the
        /// decoder's guardrail.
        /// </summary>
        private bool TryReadKittyRawSize(out int rawWidth, out int rawHeight)
        {
            rawWidth = 0;
            rawHeight = 0;
            if (!_kittyPendingParams.TryGetValue("s", out var sVal) ||
                !_kittyPendingParams.TryGetValue("v", out var vVal) ||
                !int.TryParse(sVal, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out rawWidth)
                || !int.TryParse(vVal, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out rawHeight) ||
                rawWidth <= 0 || rawHeight <= 0)
            {
                TerminalLogger.Debug("[ANSI_PARSER] Kitty raw payload missing valid s=/v= dimensions, skipping.");
                return false;
            }

            // Mirror the decoder's own pixel guardrail BEFORE inflation: a declared
            // 20000x20000 raw payload would otherwise license a >1 GB inflate that
            // DecodeRawImage only rejects afterwards.
            if (rawWidth > KittyMaxRawPixelDimension || rawHeight > KittyMaxRawPixelDimension)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] Kitty raw dimensions {rawWidth}x{rawHeight} exceed the {KittyMaxRawPixelDimension}px guardrail, skipping.");
                return false;
            }

            return true;
        }

        /// <summary>Creates a placement borrowing a retained asset; the asset owns its handle.</summary>
        private void PlaceKittyImage(KittyImageAsset asset, uint placementId)
        {
            (int width, int height) = KittyImageCells(asset.PixelWidth, asset.PixelHeight);
            var image = new TerminalImage(asset.Handle, _buffer.CursorCol, ImageAnchorRow(), width, height);
            _buffer.PlaceKittyImage(asset, image, placementId);
            bool keepCursor = _kittyPendingParams.TryGetValue("C", out var cFlag) && cFlag == "1";
            if (!keepCursor) ReserveImageCells(image, width, height);
        }

        /// <summary>
        /// How many cells an image covers: <c>c=</c> and <c>r=</c> give columns and rows, <c>w=</c> and
        /// <c>h=</c> pixels (or a percentage of the screen), and a side still missing follows from the
        /// image's aspect ratio and the cell's. Each side is clamped to 1-200.
        /// </summary>
        private (int Width, int Height) KittyImageCells(int pixelWidth, int pixelHeight)
        {
            // Mapping Kitty params to our model
            int width = 0;
            int height = 0;

            if (_kittyPendingParams.TryGetValue("c", out var cVal)) width = ParseDimension(cVal, isHeight: false);
            if (_kittyPendingParams.TryGetValue("r", out var rVal)) height = ParseDimension(rVal, isHeight: true);

            if (width == 0 && _kittyPendingParams.TryGetValue("w", out var wVal))
            {
                string wSfx = wVal.EndsWith("px", StringComparison.Ordinal) || wVal.EndsWith('%') ? wVal : wVal + "px";
                width = ParseDimension(wSfx, isHeight: false);
            }

            if (height == 0 && _kittyPendingParams.TryGetValue("h", out var hVal))
            {
                string hSfx = hVal.EndsWith("px", StringComparison.Ordinal) || hVal.EndsWith('%') ? hVal : hVal + "px";
                height = ParseDimension(hSfx, isHeight: true);
            }

            // One image pixel is one pixel of the cell CSI 16 t reports (device pixels on a scaled
            // display), as in kitty, so a client that sized its image from that reply fills its cells.
            var (effectiveCellWidth, effectiveCellHeight) = ImageCellPixels();
            double cellRatio = effectiveCellWidth / (double)effectiveCellHeight;
            double imageRatio = (double)pixelHeight / pixelWidth;

            if (width == 0 && height == 0)
            {
                width = (int)Math.Max(1, Math.Ceiling(pixelWidth / effectiveCellWidth));
                height = (int)Math.Max(1, Math.Ceiling(width * cellRatio * imageRatio));
            }
            else if (width == 0)
            {
                width = (int)Math.Max(1, Math.Ceiling(height / (cellRatio * imageRatio)));
            }
            else if (height == 0)
            {
                height = (int)Math.Max(1, Math.Ceiling(width * cellRatio * imageRatio));
            }

            width = Math.Clamp(width, 1, 200);
            height = Math.Clamp(height, 1, 200);

            return (width, height);
        }

        private void ClearKittyState()
        {
            ResetKittyPayloadBuffer();
            _kittyPendingParams.Clear();
            _kittyPayloadOverflow = false;
        }

        /// <summary>
        /// Inflates a zlib-wrapped deflate stream (kitty graphics <c>o=z</c>). Called on
        /// attacker-controlled remote input, so the output is hard-bounded at
        /// <paramref name="maxBytes"/> — a small compression bomb cannot balloon the
        /// allocation before anything validates the payload — and any failure or overage
        /// returns null for the caller to reject, rather than an exception mid-parse.
        /// Pure CPU work — no I/O, keeping VT a leaf.
        /// </summary>
        internal static byte[]? InflateZlib(byte[] compressed, int maxBytes)
        {
            try
            {
                using var source = new System.IO.MemoryStream(compressed);
                using var zlib = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
                using var output = new System.IO.MemoryStream();
                var buffer = new byte[81920];
                int total = 0;
                while (total <= maxBytes)
                {
                    // One byte past the bound is read deliberately, so a stream that is
                    // exactly the limit is accepted and anything longer is detected.
                    int n = zlib.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - total));
                    if (n <= 0) break;
                    output.Write(buffer, 0, n);
                    total += n;
                    if (total > maxBytes)
                    {
                        TerminalLogger.Debug($"[ANSI_PARSER] Kitty o=z inflate exceeded its {maxBytes} byte bound; discarding.");
                        return null;
                    }
                }

                return output.ToArray();
            }
            catch (Exception ex)
            {
                TerminalLogger.Debug($"[ANSI_PARSER] Kitty o=z inflate failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Base64 to bytes without the string copy <see cref="Convert.FromBase64String"/> needs, into
        /// an array sized exactly for well-formed input (the decoders take the whole array). Null
        /// when the text is not base64, where FromBase64String would have thrown.
        /// </summary>
        private static byte[]? DecodeBase64(ReadOnlySpan<char> chars)
        {
            int padding = chars.Length >= 2 && chars[^1] == '=' && chars[^2] == '=' ? 2
                : chars.Length >= 1 && chars[^1] == '=' ? 1
                : 0;
            var bytes = new byte[Math.Max(0, (chars.Length + 3) / 4 * 3 - padding)];
            if (!Convert.TryFromBase64Chars(chars, bytes, out int written)) return null;
            // Embedded whitespace (line-wrapped payloads) decodes shorter than the estimate.
            return written == bytes.Length ? bytes : bytes.AsSpan(0, written).ToArray();
        }
    }
}
