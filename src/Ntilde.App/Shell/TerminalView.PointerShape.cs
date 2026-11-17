using Avalonia.Input;
using Avalonia.Threading;

namespace Ntilde.Shell;

public partial class TerminalView
{
    private string? _mainPointerShape;
    private string? _alternatePointerShape;
    private readonly Dictionary<string, Cursor> _pointerShapeCursors = [];

    internal string? RequestedPointerShape => _buffer?.IsAltScreenActive == true ? _alternatePointerShape : _mainPointerShape;

    // Cursor.ToString reports the standard kind stored by Avalonia's actual Cursor object, not
    // our request/cache key. A wrong mapping therefore remains independently observable.
    internal string AppliedPointerCursor => Cursor?.ToString() is { Length: <= 64 } name ? name : "other";
    internal bool PointerShapeOverriddenByLink => _hoveredLink != null;

    internal void RequestPointerShape(string? shape, bool alternate)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_retired) return;
        if (shape is not (null or "default" or "pointer" or "text" or "crosshair")) return;
        if (alternate) _alternatePointerShape = shape;
        else _mainPointerShape = shape;
        UpdatePointerShape();
    }

    internal void ResetPointerShapes()
    {
        Dispatcher.UIThread.VerifyAccess();
        _mainPointerShape = _alternatePointerShape = null;
        UpdatePointerShape();
    }

    private Cursor PointerCursor(string shape)
    {
        if (shape == "default") return Avalonia.Input.Cursor.Default;
        if (!_pointerShapeCursors.TryGetValue(shape, out var cursor))
        {
            cursor = new Cursor(shape switch
            {
                "pointer" => StandardCursorType.Hand,
                "text" => StandardCursorType.Ibeam,
                "crosshair" => StandardCursorType.Cross,
                _ => StandardCursorType.Arrow,
            });
            _pointerShapeCursors.Add(shape, cursor);
        }
        return cursor;
    }

    private void UpdatePointerShape()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_retired) return;
        Cursor = PointerCursor(_hoveredLink != null ? "pointer" : RequestedPointerShape ?? "default");
    }

    private void ReleasePointerShapeCursors()
    {
        Cursor = Avalonia.Input.Cursor.Default;
        foreach (var cursor in _pointerShapeCursors.Values) cursor.Dispose();
        _pointerShapeCursors.Clear();
        _mainPointerShape = _alternatePointerShape = null;
    }
}
