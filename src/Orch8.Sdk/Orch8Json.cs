using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Orch8.Sdk;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> used for every request and response:
/// snake_case property names, unset (null) properties omitted, and UTC timestamps
/// written in RFC 3339 form with a <c>Z</c> suffix.
/// </summary>
public static class Orch8Json
{
    /// <summary>The serializer options the SDK uses on the wire.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Creates a fresh copy of the SDK's serializer options (safe to customise).</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new Rfc3339DateTimeOffsetConverter());
        return options;
    }

    /// <summary>Converts an arbitrary value to a <see cref="JsonNode"/> using the SDK options.</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), Options),
    };

    /// <summary>Deserializes a <see cref="JsonNode"/> into <typeparamref name="T"/> using the SDK options.</summary>
    public static T? FromNode<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Options);
}

/// <summary>
/// Writes <see cref="DateTimeOffset"/> values as UTC RFC 3339 (<c>2026-10-01T00:00:00Z</c>),
/// which is what the engine emits, and reads any ISO 8601 offset form.
/// </summary>
internal sealed class Rfc3339DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("expected an RFC 3339 timestamp");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}
