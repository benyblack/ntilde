namespace Ntilde.VT;

public partial class AnsiParser
{
    private bool _pendingCharset96;
    private bool _nationalCharset;
    private int _gr; // zero: no explicit GR invocation; slots 1..3: LS1R/LS2R/LS3R
    private int _singleShift; // zero or the G2/G3 slot for the next GL character

    private Charset ResolveCharset(int slot, bool right = false)
    {
        Charset selected = _charsets[slot];
        // VT220 peers including vttest use the 94-set A spelling for their
        // supplemental set. Keep the VT100 UK designation in G0 and G1/GL.
        return selected == Charset.Uk && !_nationalCharset && !_vt100ControlMode && (slot >= 2 || right)
            ? Charset.Latin1 : selected;
    }

    private bool IsLatin1LeftGraphic => !_buffer.Modes.IsVt52
        && ResolveCharset(_singleShift != 0 ? _singleShift : _gl) == Charset.Latin1;

    private char TranslateGraphic(char c, bool rawGraphic)
    {
        if (_buffer.Modes.IsVt52) return _vt52Graphics ? MapDecSpecialGraphics(c) : c;
        if (rawGraphic && c >= 0xa0 && _gr != 0)
        {
            Charset gr = ResolveCharset(_gr, right: true);
            return gr == Charset.Latin1 || c is '\xa0' or '\xff' ? c : MapCharset(gr, (char)(c - 0x80));
        }
        if (c > 0x7f) return c;

        Charset gl = ResolveCharset(_singleShift != 0 ? _singleShift : _gl);
        _singleShift = 0;
        return MapCharset(gl, c);
    }

    private void SelectSingleShift(int slot)
    {
        _singleShift = slot;
        _lastGraphicChar = null;
    }

    private bool TryHandleCharsetShift(char c)
    {
        switch (c)
        {
            case 'N': SelectSingleShift(2); break;
            case 'O': SelectSingleShift(3); break;
            case 'n': _gl = 2; break;
            case 'o': _gl = 3; break;
            case '~': SelectRightShift(1); break;
            case '}': SelectRightShift(2); break;
            case '|': SelectRightShift(3); break;
            default: return false;
        }
        _lastGraphicChar = null;
        _state = State.Normal;
        return true;
    }

    private void SelectRightShift(int slot)
    {
        _gr = slot;
        // A new GR invocation after DOCS UTF-8 requests a legacy right
        // half again, while valid UTF-8 remains unambiguous.
        if (_outputCoding == OutputCoding.Utf8) _outputCoding = OutputCoding.Automatic;
    }

    internal static void ValidateSingleShift(int slot)
    {
        if (slot is not (0 or 2 or 3))
            throw StateTransferValidation.Reject("single shift", "expected no shift, G2 or G3");
    }
}
