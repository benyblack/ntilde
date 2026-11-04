using System;
using System.Globalization;

namespace Ntilde.VT;

public partial class AnsiParser
{
    private readonly record struct KittyCommandIdentity(uint Id, uint Number, uint Placement, int Quiet, bool HasIdentity);

    private bool TryReadKittyIdentity(out KittyCommandIdentity identity)
    {
        bool validId = TryReadKittyUInt("i", out uint id);
        bool validNumber = TryReadKittyUInt("I", out uint number);
        bool validPlacement = TryReadKittyUInt("p", out uint placement);
        bool validQuiet = TryReadKittyUInt("q", out uint quiet) && quiet <= 2;
        bool hasId = _kittyPendingParams.ContainsKey("i"), hasNumber = _kittyPendingParams.ContainsKey("I");
        identity = new(id, number, placement, validQuiet ? (int)quiet : 0, id != 0 || number != 0);
        if (validId && validNumber && validPlacement && validQuiet && !(hasId && hasNumber)) return true;
        ReplyKittyCommand(identity with { HasIdentity = true }, "EINVAL:Invalid graphics identity or quiet policy");
        return false;
    }

    private bool TryReadKittyUInt(string key, out uint value)
    {
        value = 0;
        return !_kittyPendingParams.TryGetValue(key, out string? text)
            || (text.Length is > 0 and <= KittyMaxEchoedIdChars
                && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value));
    }

    private void ReplyKittyCommand(KittyCommandIdentity identity, string status)
    {
        bool success = status == "OK";
        if (!identity.HasIdentity || identity.Quiet == 2 || (success && identity.Quiet == 1)) return;
        string fields = "i=" + identity.Id.ToString(CultureInfo.InvariantCulture);
        if (identity.Number != 0) fields += ",I=" + identity.Number.ToString(CultureInfo.InvariantCulture);
        if (identity.Placement != 0) fields += ",p=" + identity.Placement.ToString(CultureInfo.InvariantCulture);
        OnResponse?.Invoke($"\x1b_G{fields};{status}\x1b\\");
    }

    private string DisplayStoredKittyImage(KittyCommandIdentity identity, out uint id)
    {
        id = identity.Id;
        KittyImageAsset? asset = _buffer.FindKittyImage(identity.Id, identity.Number);
        if (asset is null) return "ENOENT:Image not found";
        id = asset.Id;
        PlaceKittyImage(asset, identity.Placement);
        return "OK";
    }

    private string DeleteStoredKittyImages(KittyCommandIdentity identity)
    {
        string selection = _kittyPendingParams.TryGetValue("d", out string? value) ? value : "a";
        if (selection is not ("a" or "A" or "i" or "I" or "n" or "N")) return "ENOTSUP:Deletion selection not supported";
        bool visibleOnly = selection is "a" or "A";
        if (!visibleOnly && (selection is "i" or "I" ? identity.Id == 0 : identity.Number == 0))
            return "EINVAL:Deletion requires the selected image identity";
        _buffer.DeleteKittyImages(identity.Id, identity.Number, identity.Placement,
            freeData: selection is "A" or "I" or "N", visibleOnly);
        return "OK";
    }
}
