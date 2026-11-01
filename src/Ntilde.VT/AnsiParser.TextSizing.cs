namespace Ntilde.VT;

public partial class AnsiParser
{
    private void HandleTextSizing(string data)
    {
        int separator = data.IndexOf(';');
        if (separator < 0 || !TextSizingOptions.TryParse(data.AsSpan(0, separator), out var options)) return;
        ReadOnlySpan<char> text = data.AsSpan(separator + 1);
        if (TextSizingOptions.IsSafeText(text)) _buffer.WriteSizedText(text.ToString(), options);
    }
}
