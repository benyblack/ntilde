using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ntilde.VT
{
    /// <summary>
    /// Kitty's color-control protocol, <c>OSC 21 ; key=value ; key=value ... ST</c>, over the runtime colors OSC 4, 10, 11, 12,
    /// 17 and 19 already keep. The keys are <c>foreground</c>, <c>background</c>, <c>selection_background</c>,
    /// <c>selection_foreground</c>, <c>cursor</c> and the palette indices 0-255. A value of <c>?</c> queries, a bare key resets the
    /// override, and a numeric color (the encodings OSC 10 accepts) sets it. The reply is one OSC 21 carrying one field per query
    /// (and one <c>unknown=</c> field per key the terminal has no color for), in request order, ended by ST.
    /// </summary>
    public partial class AnsiParser
    {
        /// <summary>A longer sequence is dropped whole, like an oversized OSC 4: the reply can never amplify it.</summary>
        private const int KittyColorMaxPayload = 4096;

        private const int KittyColorMaxFields = 256;
        private const int KittyColorForeground = 256;
        private const int KittyColorBackground = 257;
        private const int KittyColorSelectionBackground = 258;
        private const int KittyColorSelectionForeground = 259;
        private const int KittyColorCursor = 260;

        private void HandleKittyColors(string data)
        {
            if (data.Length > KittyColorMaxPayload) return;
            TerminalColorOverrides colors = _buffer.CopyColorOverrides();
            List<string> fields = [];
            bool changed = false;
            int seen = 0;
            foreach (string field in data.Split(';'))
            {
                if (field.Length == 0) continue;
                if (++seen > KittyColorMaxFields) break;
                int equals = field.IndexOf('=');
                string key = equals < 0 ? field : field[..equals];
                string? value = equals < 0 ? null : field[(equals + 1)..];
                if (!TryKittyColorSlot(key, out int slot))
                {
                    // The key is Base64-encoded (no padding) so the terminal cannot be made to emit arbitrary text.
                    fields.Add("unknown=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('='));
                }
                else if (value == "?")
                {
                    fields.Add(KittyColorName(slot) + "=" + FormatKittyColor(ResolveKittyColor(slot, colors)));
                }
                else
                {
                    changed |= ApplyKittyColor(colors, slot, value);
                }
            }

            if (changed)
            {
                _buffer.ApplyColorOverrides(colors);
                RefreshColorScheme();
            }

            if (fields.Count > 0) OnResponse?.Invoke("\x1b]21;" + string.Join(';', fields) + "\x1b\\");
        }

        private static bool TryKittyColorSlot(string key, out int slot)
        {
            slot = key switch
            {
                "foreground" => KittyColorForeground,
                "background" => KittyColorBackground,
                "selection_background" => KittyColorSelectionBackground,
                "selection_foreground" => KittyColorSelectionForeground,
                "cursor" => KittyColorCursor,
                _ => -1
            };
            if (slot >= 0) return true;
            return key.Length is >= 1 and <= 3 && TryPaletteIndex(key, out slot);
        }

        private static string KittyColorName(int slot) => slot switch
        {
            KittyColorForeground => "foreground",
            KittyColorBackground => "background",
            KittyColorSelectionBackground => "selection_background",
            KittyColorSelectionForeground => "selection_foreground",
            KittyColorCursor => "cursor",
            _ => slot.ToString(CultureInfo.InvariantCulture)
        };

        private static int KittyDynamicCode(int slot) => slot switch
        {
            KittyColorForeground => 10,
            KittyColorBackground => 11,
            KittyColorSelectionBackground => 17,
            KittyColorSelectionForeground => 19,
            _ => 12
        };

        /// <summary>The color a query reports: an empty value when no concrete color exists (an unset derived selection color).</summary>
        private TermColor? ResolveKittyColor(int slot, TerminalColorOverrides colors) =>
            slot < 256 ? _buffer.GetPaletteColor(slot, colors) : ResolveDynamicColor(KittyDynamicCode(slot), colors);

        private static string FormatKittyColor(TermColor? color) => color is { } c
            ? string.Create(CultureInfo.InvariantCulture, $"rgb:{c.R:x2}/{c.G:x2}/{c.B:x2}")
            : string.Empty;

        /// <summary>
        /// Sets (a numeric color) or resets (no <c>=</c>) one override and reports whether a change was attempted. An empty value
        /// (kitty's "dynamic" color), a color name and an alpha suffix are not modeled and leave the color alone.
        /// </summary>
        private static bool ApplyKittyColor(TerminalColorOverrides colors, int slot, string? value)
        {
            TermColor? color = null;
            if (value is not null)
            {
                if (!TryParseColor(value, out TermColor parsed)) return false;
                color = parsed;
            }

            if (slot < 256) colors.Palette[slot] = color?.ToUint();
            else SetDynamicOverride(colors, KittyDynamicCode(slot), color);
            return true;
        }
    }
}
