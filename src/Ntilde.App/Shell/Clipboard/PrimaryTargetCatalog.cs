using Ntilde.VT;

namespace Ntilde.Shell.Clipboard;

internal sealed class PrimaryTargetCatalog(uint utf8, uint plain, Func<uint, string> name, Func<string, uint> find)
{
    // A hostile owner can advertise many non-MIME targets. Standard text is
    // selected directly, before the bounded optional-name scan and MIME cap.
    private const string TextMime = "text/plain";
    private const int MaxNameLookups = 512;

    public Dictionary<string, uint> Select(uint[] targets, IReadOnlyList<string>? requested)
    {
        var available = targets.ToHashSet();
        if (requested?.Count > 0) return SelectRequested(available, requested);
        var formats = new Dictionary<string, uint>(StringComparer.Ordinal);
        AddText(available, formats);
        foreach (uint atom in targets.Distinct().Take(MaxNameLookups))
        {
            if (formats.Count == KittyClipboardPacket.MaxTypes) break;
            string mime = TargetName(atom);
            if (KittyClipboardPacket.IsMime(mime)) formats.TryAdd(mime, atom);
        }
        return formats;
    }

    private string TargetName(uint atom)
    {
        try { return atom == utf8 || atom == 31 ? TextMime : name(atom); }
        catch (PrimaryInvalidAtomException) { return ""; }
    }

    private Dictionary<string, uint> SelectRequested(HashSet<uint> available, IReadOnlyList<string> requested)
    {
        var formats = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (string mime in requested.Distinct(StringComparer.Ordinal).Take(KittyClipboardPacket.MaxTypes))
        {
            if (mime == TextMime) AddText(available, formats);
            else
            {
                uint atom = find(mime); // Only-if-exists lookup: a read creates no client-named atoms.
                if (atom != 0 && available.Contains(atom)) formats[mime] = atom;
            }
        }
        return formats;
    }

    private void AddText(HashSet<uint> available, Dictionary<string, uint> formats)
    {
        uint atom = new[] { plain, utf8, 31u }.FirstOrDefault(candidate => candidate != 0 && available.Contains(candidate));
        if (atom != 0) formats[TextMime] = atom;
    }
}

// Only a server BadAtom is recoverable during the optional target-name scan.
// Connection failures and deadlines must still abort the operation.
internal sealed class PrimaryInvalidAtomException : IOException;
