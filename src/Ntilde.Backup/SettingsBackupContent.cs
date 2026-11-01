using System.Text.Json;

namespace Ntilde.Backup;

/// <summary>Archive-only settings bytes; never changes the source or opens credential storage.</summary>
internal static class SettingsBackupContent
{
    internal const string BundlePath = "settings/settings.json";

    internal static byte[] Read(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        try
        {
            // Match settings' BOM-aware reading, including legacy UTF-16 files.
            using var reader = new StreamReader(new MemoryStream(original));
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw UnsafeSettings();
            using var output = new MemoryStream();
            bool removed = false;
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name.Equals("Profiles", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind != JsonValueKind.Null)
                    {
                        if (property.Value.ValueKind != JsonValueKind.Array) throw UnsafeSettings();
                        writer.WriteStartArray();
                        foreach (JsonElement profile in property.Value.EnumerateArray())
                        {
                            if (profile.ValueKind == JsonValueKind.Null) { writer.WriteNullValue(); continue; }
                            if (profile.ValueKind != JsonValueKind.Object) throw UnsafeSettings();
                            writer.WriteStartObject();
                            foreach (JsonProperty field in profile.EnumerateObject())
                            {
                                // Enumerate rather than GetProperty: duplicate and escaped keys
                                // must not leave another copy of a legacy credential behind.
                                if (field.Name.Equals("Password", StringComparison.OrdinalIgnoreCase)) removed = true;
                                else field.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return removed ? output.ToArray() : original;
        }
        catch (JsonException)
        {
            // Do not put credential-bearing JSON or parser diagnostics into the outcome/log.
            throw UnsafeSettings();
        }
    }

    private static InvalidDataException UnsafeSettings() => new("Cannot safely export settings: expected a settings object with a profile array.");
}
