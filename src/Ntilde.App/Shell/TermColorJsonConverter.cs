using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Ntilde.VT;

namespace Ntilde.Shell
{
    public sealed class TermColorJsonConverter : JsonConverter<TermColor>
    {
        public override TermColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string? s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return TermColor.Transparent;
                try
                {
                    var color = Color.Parse(s);
                    return TermColorHelper.FromAvaloniaColor(color);
                }
                catch
                {
                    return TermColor.Transparent;
                }
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                // Backward-compatible object parsing for legacy/custom payloads.
                using var doc = JsonDocument.ParseValue(ref reader);
                return TryReadObject(doc.RootElement, out var color) ? color : TermColor.Transparent;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                return TermColor.Transparent;
            }

            throw new JsonException($"Unsupported token {reader.TokenType} for {nameof(TermColor)}.");
        }

        /// <summary>
        /// The legacy <c>{R,G,B,A}</c> object form (either casing). False when the object names no
        /// color channel at all; a fully transparent <c>{"A":0}</c> is a color, not a failure.
        /// </summary>
        internal static bool TryReadObject(JsonElement element, out TermColor color)
        {
            byte r = 0, g = 0, b = 0, a = 255;
            bool hasAny = false;

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                switch (prop.Name)
                {
                    case "R":
                    case "r":
                        if (prop.Value.TryGetByte(out var rr)) { r = rr; hasAny = true; }
                        break;
                    case "G":
                    case "g":
                        if (prop.Value.TryGetByte(out var gg)) { g = gg; hasAny = true; }
                        break;
                    case "B":
                    case "b":
                        if (prop.Value.TryGetByte(out var bb)) { b = bb; hasAny = true; }
                        break;
                    case "A":
                    case "a":
                        if (prop.Value.TryGetByte(out var aa)) { a = aa; hasAny = true; }
                        break;
                }
            }

            color = hasAny ? new TermColor(r, g, b, a) : TermColor.Transparent;
            return hasAny;
        }

        public override void Write(Utf8JsonWriter writer, TermColor value, JsonSerializerOptions options)
        {
            // Keep theme files human-editable and compatible with existing #RRGGBB format.
            writer.WriteStringValue(value.ToHex());
        }
    }
}
