using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Ntilde.VT;

namespace Ntilde.Shell
{
    /// <summary>
    /// The theme's optional colors (<see cref="TerminalTheme.SelectionBackground"/> and the rest).
    /// Unlike <see cref="TermColorJsonConverter"/>, which turns a malformed value into Transparent,
    /// a malformed optional value reads as null - "derive it" - with a logged warning, so one bad
    /// entry in a hand-edited theme costs that color, never the theme.
    /// </summary>
    public sealed class OptionalTermColorJsonConverter : JsonConverter<TermColor?>
    {
        public override bool HandleNull => true;

        public override TermColor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;

                case JsonTokenType.String:
                    string? text = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(text) && Color.TryParse(text, out var color))
                    {
                        return TermColorHelper.FromAvaloniaColor(color);
                    }

                    Warn($"\"{text}\"");
                    return null;

                case JsonTokenType.StartObject:
                    // The legacy {R,G,B,A} form, as the required fields accept it.
                    using (var document = JsonDocument.ParseValue(ref reader))
                    {
                        if (TermColorJsonConverter.TryReadObject(document.RootElement, out var parsed))
                        {
                            return parsed;
                        }
                    }

                    Warn("an object with no color channels");
                    return null;

                default:
                    Warn($"a {reader.TokenType} value");
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, TermColor? value, JsonSerializerOptions options)
        {
            if (value is { } color)
            {
                writer.WriteStringValue(color.ToHex());
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        private static void Warn(string what)
            => TerminalLogger.Warning($"[Theme] An optional theme color is {what}, not a color; it is derived instead.");
    }
}
