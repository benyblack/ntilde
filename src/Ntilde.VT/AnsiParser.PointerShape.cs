namespace Ntilde.VT;

public partial class AnsiParser
{
    /// <summary>
    /// A bounded OSC 22 basic-set request for the active screen. The host owns pointer state and
    /// presentation; null releases its override. No stacks, queries, X11 aliases or other names.
    /// </summary>
    public Action<string?, bool>? OnPointerShapeRequested { get; set; }

    /// <summary>RIS/DECSTR withdraw both host-owned screen overrides; no native UI belongs in VT.</summary>
    public Action? OnPointerShapeReset { get; set; }

    private void HandlePointerShape(string data)
    {
        if (data.Length > 32) return;
        if (data.StartsWith('=')) data = data[1..];
        if (data.Length == 0 || data is "default" or "pointer" or "text" or "crosshair")
            OnPointerShapeRequested?.Invoke(data.Length == 0 ? null : data, _buffer.IsAltScreenActive);
    }
}
