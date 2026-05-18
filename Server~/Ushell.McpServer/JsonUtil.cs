using System.Collections;
using System.Text.Json;

namespace Ushell.McpServer;

internal static class JsonUtil
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public static object? Deserialize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ConvertElement(document.RootElement);
    }

    public static string Serialize(object? value)
    {
        return JsonSerializer.Serialize(Normalize(value), Options);
    }

    public static Dictionary<string, object?> AsObject(object? value)
    {
        return value as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }

    public static object? Get(Dictionary<string, object?> values, string key)
    {
        return values.TryGetValue(key, out object? value) ? value : null;
    }

    private static object? ConvertElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> dictionary = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    dictionary[property.Name] = ConvertElement(property.Value);
                }

                return dictionary;
            case JsonValueKind.Array:
                List<object?> list = new();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(ConvertElement(item));
                }

                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long integer))
                {
                    return integer;
                }

                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static object? Normalize(object? value)
    {
        if (value == null || value is string || value is bool || value is char)
        {
            return value;
        }

        if (value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong ||
            value is float || value is double || value is decimal)
        {
            return value;
        }

        if (value is IDictionary dictionary)
        {
            Dictionary<string, object?> normalized = new(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                normalized[entry.Key?.ToString() ?? string.Empty] = Normalize(entry.Value);
            }

            return normalized;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            List<object?> normalized = new();
            foreach (object? item in enumerable)
            {
                normalized.Add(Normalize(item));
            }

            return normalized;
        }

        return value.ToString();
    }
}
