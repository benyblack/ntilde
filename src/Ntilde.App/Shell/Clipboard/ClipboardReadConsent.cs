using System.Security.Cryptography;
using System.Text;

namespace Ntilde.Shell.Clipboard;

internal enum ClipboardReadConsent { Deny, AllowOnce, AllowSession }

internal sealed record ClipboardReadRequest(string Name, bool CanRemember);

/// <summary>Bounded, memory-only read grants belonging to one terminal session.</summary>
internal sealed class ClipboardSessionPermissions
{
    internal const int Capacity = 32;
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public static string? Key(string name, string password)
    {
        if (name.Length == 0 || password.Length == 0) return null;
        // Hash separately to avoid ambiguity when either UTF-8 string contains a delimiter.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))) +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    }

    public bool Contains(string key) => _keys.Contains(key);
    public void Allow(string key)
    {
        if (!_keys.Add(key)) return;
        _order.Enqueue(key);
        if (_order.Count > Capacity) _keys.Remove(_order.Dequeue());
    }
    public void Clear() { _keys.Clear(); _order.Clear(); }
}
