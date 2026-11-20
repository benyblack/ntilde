using System.Buffers;
using System.Text;

namespace Ntilde.VT;

public partial class AnsiParser
{
    private enum OutputCoding { Automatic, Latin1, Utf8 }
    private OutputCoding _outputCoding;
    private readonly byte[] _outputTail = new byte[3];
    private int _outputTailLength;

    /// <summary>Complete snapshots pair these held bytes with the parser and buffer at the same cut.</summary>
    public ReadOnlySpan<byte> PendingOutputBytes => _outputTail.AsSpan(0, _outputTailLength);

    /// <summary>Adopts the incomplete UTF-8 prefix for a new output stream position.</summary>
    public void RestoreOutputTail(ReadOnlySpan<byte> tail)
    {
        ValidateOutputTail(tail);
        tail.CopyTo(_outputTail);
        _outputTailLength = tail.Length;
    }

    internal static void ValidateOutputTail(ReadOnlySpan<byte> tail)
    {
        if (!tail.IsEmpty && (tail.Length > 3 || Rune.DecodeFromUtf8(tail, out _, out _) != OperationStatus.NeedMoreData))
            throw StateTransferValidation.Reject("output decoder tail", "expected an incomplete UTF-8 prefix");
    }

    /// <summary>Parses original terminal output, using UTF-8 unless legacy coding or a graphic shift applies.</summary>
    /// <returns>Whether a character or control was delivered to the parser.</returns>
    public bool ProcessBytes(ReadOnlySpan<byte> bytes) => !bytes.IsEmpty && ProcessOutput(default, bytes, fromBytes: true);

    private bool TryReadOutputByte(ref ReadOnlySpan<byte> bytes, ref char lowSurrogate, out char c, out bool rawGraphic)
    {
        rawGraphic = false;
        if (lowSurrogate != '\0')
        {
            c = lowSurrogate;
            lowSurrogate = '\0';
            return true;
        }

        c = default;
        if (bytes.IsEmpty) return false;
        if (_outputTailLength == 0 && (bytes[0] < 0x80 || _outputCoding == OutputCoding.Latin1))
        {
            c = (char)bytes[0];
            rawGraphic = bytes[0] >= 0xa0;
            bytes = bytes[1..];
            return true;
        }

        Span<byte> splice = stackalloc byte[4];
        scoped ReadOnlySpan<byte> candidate = bytes;
        if (_outputTailLength != 0)
        {
            int copied = Math.Min(bytes.Length, splice.Length - _outputTailLength);
            PendingOutputBytes.CopyTo(splice);
            bytes[..copied].CopyTo(splice[_outputTailLength..]);
            candidate = splice[..(_outputTailLength + copied)];
        }

        OperationStatus status = Rune.DecodeFromUtf8(candidate, out Rune rune, out int consumed);
        if (status == OperationStatus.NeedMoreData)
        {
            candidate.CopyTo(_outputTail);
            _outputTailLength = candidate.Length;
            bytes = default;
            return false;
        }

        if (status == OperationStatus.InvalidData && _outputCoding == OutputCoding.Automatic
            && _state == State.Normal && !_buffer.Modes.IsVt52)
        {
            byte first = candidate[0];
            rawGraphic = first >= 0xa0 && _gr != 0 && _charsets[_gr] != Charset.Ascii;
            // An isolated SS2 or SS3 can be an eight-bit control. A UTF-8 continuation is
            // never seen here on its own when it belongs to a valid scalar.
            if (rawGraphic || (first is 0x8e or 0x8f && _charsets[first - 0x8c] != Charset.Ascii))
            {
                rune = new Rune(first);
                consumed = 1;
            }
        }

        if (consumed < _outputTailLength)
        {
            _outputTail.AsSpan(consumed, _outputTailLength - consumed).CopyTo(_outputTail);
            _outputTailLength -= consumed;
        }
        else
        {
            bytes = bytes[(consumed - _outputTailLength)..];
            _outputTailLength = 0;
        }

        if (rune.IsBmp)
        {
            c = (char)rune.Value;
        }
        else
        {
            int supplementary = rune.Value - 0x10000;
            c = (char)(0xd800 + (supplementary >> 10));
            lowSurrogate = (char)(0xdc00 + (supplementary & 0x3ff));
        }
        return true;
    }
}
