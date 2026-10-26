using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Ntilde.McpServer.Tools;

[McpServerToolType]
public sealed partial class ThemeTools
{
    // The 16 ANSI colors plus the 3 UI colors. All are required in a Ntilde theme JSON.
    internal static readonly string[] ColorFields =
    {
        "Foreground", "Background", "CursorColor",
        "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
        "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow",
        "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite",
    };

    // Theme v2's optional colors: omitted (or null) means Ntilde derives them from the colors above.
    internal static readonly string[] OptionalColorFields =
    {
        "SelectionBackground", "SelectionForeground", "CursorText", "SearchMatch", "SearchMatchActive",
        "Accent", "Success", "Warning", "Error", "Info",
    };

    // The optional colors drawn as UI text on panels; an explicit one should still read there.
    private static readonly string[] UiTextColorFields = { "Accent", "Success", "Warning", "Error", "Info" };

    // WCAG AA for normal text, the bar Ntilde's derived UI colors are held to.
    private const double TextContrast = 4.5;

    // Accept "#RGB", "#RRGGBB" and "#AARRGGBB": what the app's theme loader (Avalonia Color.Parse) reads.
    // Not the scRGB "sc#r,g,b" form, which it does not parse: a theme using it loaded as transparent black.
    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorRegex();

    [McpServerTool(Name = "ntilde.get_theme_schema"),
     Description("Returns the schema for a Ntilde theme JSON file: required fields (Name + 16 ANSI colors + Foreground/Background/CursorColor), the optional colors Ntilde derives when omitted (selection, search, cursor text, accent, status), the accepted color string format, and an example. Use before authoring or editing a theme.")]
    public static string GetThemeSchema() =>
        """
        # Ntilde theme JSON schema

        A theme is a JSON object. The fields in the first table are required; the optional colors
        in the second are derived when omitted.

        | Field | Type | Notes |
        |-------|------|-------|
        | `Name` | string | Display name, e.g. "Dracula". Non-empty. |
        | `Foreground` | color | Default text color. |
        | `Background` | color | Terminal background. |
        | `CursorColor` | color | Cursor color. |
        | `Black`,`Red`,`Green`,`Yellow`,`Blue`,`Magenta`,`Cyan`,`White` | color | Standard ANSI 0–7. |
        | `BrightBlack`…`BrightWhite` | color | Bright ANSI 8–15 (same 8 names, `Bright`-prefixed). |

        ## Optional colors (derived when omitted)

        | Field | Notes | Derived from |
        |-------|-------|--------------|
        | `SelectionBackground` | Selection fill; selected text is drawn on top of it. | The accent at alpha 0x66. |
        | `SelectionForeground` | Selected text. | Omitted: each selected cell keeps its own color. |
        | `CursorText` | The glyph under a block cursor. | `Background`. |
        | `SearchMatch` | Search match fill. | The warning color at alpha 0x64. |
        | `SearchMatchActive` | The current search match. | Between warning and error, at alpha 0x96. |
        | `Accent` | UI accent: buttons, links, the selected tab. | A softened `Blue`. |
        | `Success`, `Warning`, `Error`, `Info` | UI status colors. | Softened `Green`, `Yellow`, `Red`, `Cyan`. |

        Derived colors are adjusted until they read on the theme's panels; an explicit value is used
        as given, so pick one with at least 4.5:1 contrast against the panel (the validator warns).

        **Color format:** `#RGB`, `#RRGGBB`, or `#AARRGGBB` (hex).

        ## Example
        ```json
        {
          "Name": "Dracula",
          "Foreground": "#F8F8F2", "Background": "#282A36", "CursorColor": "#F8F8F2",
          "Black": "#21222C", "Red": "#FF5555", "Green": "#50FA7B", "Yellow": "#F1FA8C",
          "Blue": "#BD93F9", "Magenta": "#FF79C6", "Cyan": "#8BE9FD", "White": "#F8F8F2",
          "BrightBlack": "#6272A4", "BrightRed": "#FF6E6E", "BrightGreen": "#69FF94",
          "BrightYellow": "#FFFFA5", "BrightBlue": "#D6ACFF", "BrightMagenta": "#FF92DF",
          "BrightCyan": "#A4FFFF", "BrightWhite": "#FFFFFF"
        }
        ```
        """;

    [McpServerTool(Name = "ntilde.validate_theme_json"),
     Description("Validates a Ntilde theme JSON string against the theme schema. Reports missing required fields, invalid color values, and unknown fields. Returns a structured pass/fail report.")]
    public static string ValidateThemeJson(
        [Description("The full theme JSON document to validate.")] string themeJson)
    {
        if (string.IsNullOrWhiteSpace(themeJson))
        {
            return "INVALID: empty input — expected a theme JSON object.";
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(themeJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return $"INVALID: not valid JSON — {ex.Message}";
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return $"INVALID: top-level value must be a JSON object, but was {root.ValueKind}.";
        }

        var errors = new List<string>();
        var warnings = new List<string>();

        // Name
        if (!root.TryGetProperty("Name", out var nameEl))
        {
            errors.Add("Missing required field 'Name'.");
        }
        else if (nameEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            errors.Add("Field 'Name' must be a non-empty string.");
        }

        // Colors
        foreach (var field in ColorFields)
        {
            if (!root.TryGetProperty(field, out var el))
            {
                errors.Add($"Missing required color field '{field}'.");
                continue;
            }
            if (el.ValueKind != JsonValueKind.String)
            {
                errors.Add($"Field '{field}' must be a color string, but was {el.ValueKind}.");
                continue;
            }
            var value = el.GetString() ?? string.Empty;
            if (!IsValidColor(value))
            {
                errors.Add($"Field '{field}' has an invalid color value '{value}' (expected #RGB/#RRGGBB/#AARRGGBB).");
            }
        }

        // Optional colors: absent or null is fine (derived); a present value must parse.
        foreach (var field in OptionalColorFields)
        {
            if (!root.TryGetProperty(field, out var el) || el.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (el.ValueKind != JsonValueKind.String)
            {
                errors.Add($"Optional field '{field}' must be a color string or null, but was {el.ValueKind}.");
                continue;
            }
            var value = el.GetString() ?? string.Empty;
            if (!IsValidColor(value))
            {
                errors.Add($"Optional field '{field}' has an invalid color value '{value}' (expected #RGB/#RRGGBB/#AARRGGBB).");
            }
        }

        AddContrastWarnings(root, warnings);

        // Unknown fields → warnings (forward-compatible, not fatal)
        var known = new HashSet<string>(ColorFields.Concat(OptionalColorFields)) { "Name" };
        foreach (var prop in root.EnumerateObject())
        {
            if (!known.Contains(prop.Name))
            {
                warnings.Add($"Unknown field '{prop.Name}' (ignored).");
            }
        }

        var sb = new StringBuilder();
        sb.Append(errors.Count == 0 ? "VALID" : "INVALID").Append('\n');
        if (errors.Count > 0)
        {
            sb.Append("\nErrors:\n");
            foreach (var e in errors) sb.Append("  - ").Append(e).Append('\n');
        }
        if (warnings.Count > 0)
        {
            sb.Append("\nWarnings:\n");
            foreach (var w in warnings) sb.Append("  - ").Append(w).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Warns (never fails) when an explicit accent or status color misses <see cref="TextContrast"/>
    /// against the panel Ntilde derives for the theme: explicit values are used verbatim, so nothing
    /// fixes them up the way derived ones are. Only hex values are measured.
    /// </summary>
    private static void AddContrastWarnings(JsonElement root, List<string> warnings)
    {
        if (!TryGetHex(root, "Background", out var background) || !TryGetHex(root, "Foreground", out var foreground))
        {
            return;
        }

        var panel = DerivePanel(background, foreground);
        foreach (var field in UiTextColorFields)
        {
            if (!TryGetHex(root, field, out var color))
            {
                continue;
            }

            double ratio = Contrast(color, panel);
            if (ratio < TextContrast)
            {
                warnings.Add($"Optional field '{field}' has {ratio:0.0}:1 contrast against the theme's panels (#{panel.R:X2}{panel.G:X2}{panel.B:X2}); UI text in it may be hard to read. Aim for at least {TextContrast}:1.");
            }
        }
    }

    /// <summary>
    /// The panel surface ThemeTokens.Derive gives a theme: the background shifted 18 per channel,
    /// lighter on dark themes and darker on light ones, flipped when that would leave the
    /// foreground under 4.5:1. Mirrored here because this server does not reference Ntilde.App;
    /// ThemeToolsPanelMirrorTests pins it to the real derivation for every shipped theme.
    /// </summary>
    internal static (byte R, byte G, byte B) DerivePanel((byte R, byte G, byte B) background, (byte R, byte G, byte B) foreground)
    {
        bool dark = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0 < 0.5;
        int direction = dark ? 1 : -1;
        double usual = Contrast(foreground, Shift(background, 18 * direction));
        if (usual < TextContrast && Contrast(foreground, Shift(background, -18 * direction)) > usual)
        {
            direction = -direction;
        }

        return Shift(background, 18 * direction);
    }

    private static (byte R, byte G, byte B) Shift((byte R, byte G, byte B) c, int delta)
    {
        static byte Clamp(int v) => (byte)System.Math.Clamp(v, 0, 255);
        return (Clamp(c.R + delta), Clamp(c.G + delta), Clamp(c.B + delta));
    }

    private static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (System.Math.Max(la, lb) + 0.05) / (System.Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((byte R, byte G, byte B) c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>Reads a #RGB/#RRGGBB/#AARRGGBB field's RGB; false for anything else (absent, null, not hex).</summary>
    private static bool TryGetHex(JsonElement root, string field, out (byte R, byte G, byte B) color)
    {
        color = default;
        if (!root.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = el.GetString() ?? string.Empty;
        if (!HexColorRegex().IsMatch(value))
        {
            return false;
        }

        var hex = value[1..];
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(ch => new string(ch, 2)));
        }

        // Eight digits are #AARRGGBB, the order Ntilde reads and writes; alpha is not measured.
        int rgb = hex.Length == 8 ? 2 : 0;
        static byte At(string h, int i) => System.Convert.ToByte(h.Substring(i, 2), 16);
        color = (At(hex, rgb), At(hex, rgb + 2), At(hex, rgb + 4));
        return true;
    }

    private static bool IsValidColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (HexColorRegex().IsMatch(value)) return true;
        return false;
    }
}
