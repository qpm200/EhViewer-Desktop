using System.Globalization;
using System.Text.Json;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Shared parsing utilities matching upstream <c>ParserUtils</c> plus JSON extraction helpers.
/// </summary>
public static class ParserUtils
{
    public const string DateFormatPattern = "yyyy-MM-dd HH:mm";

    public static string FormatDate(long timeMs) =>
        FormatDate(timeMs, TimeZoneInfo.Local);

    public static string FormatDate(long timeMs, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        DateTimeOffset dto = TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeMilliseconds(timeMs),
            timeZone);
        return dto.ToString(DateFormatPattern, CultureInfo.InvariantCulture);
    }

    public static string Trim(string? str)
    {
        string input = str ?? string.Empty;
        return (StringUtils.UnescapeXml(input) ?? string.Empty).Trim();
    }

    public static int ParseInt(string? str, int defValue) =>
        NumberUtils.ParseIntSafely(Trim(str).Replace(",", string.Empty, StringComparison.Ordinal), defValue);

    public static long ParseLong(string? str, long defValue) =>
        NumberUtils.ParseLongSafely(Trim(str).Replace(",", string.Empty, StringComparison.Ordinal), defValue);

    public static float ParseFloat(string? str, float defValue) =>
        NumberUtils.ParseFloatSafely(Trim(str).Replace(",", string.Empty, StringComparison.Ordinal), defValue);

    internal static JsonElement ParseJsonObject(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected JSON object root.");
        }
        return root.Clone();
    }

    internal static JsonElement GetRequiredProperty(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new JsonException($"Missing or null JSON property '{name}'.");
        }
        return value;
    }

    internal static string GetRequiredString(JsonElement obj, string name)
    {
        JsonElement el = GetRequiredProperty(obj, name);
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString()!,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
            _ => throw new JsonException($"JSON property '{name}' is not a scalar value.")
        };
    }

    internal static string? GetOptionalString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
            _ => throw new JsonException($"JSON property '{name}' is not a scalar value.")
        };
    }

    internal static int GetRequiredInt32(JsonElement obj, string name)
    {
        JsonElement el = GetRequiredProperty(obj, name);
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int intVal))
        {
            return intVal;
        }
        if (el.ValueKind == JsonValueKind.String &&
            int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }
        throw new JsonException($"JSON property '{name}' is not a valid Int32.");
    }

    internal static long GetRequiredInt64(JsonElement obj, string name)
    {
        JsonElement el = GetRequiredProperty(obj, name);
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long longVal))
        {
            return longVal;
        }
        if (el.ValueKind == JsonValueKind.String &&
            long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }
        throw new JsonException($"JSON property '{name}' is not a valid Int64.");
    }

    internal static float GetRequiredSingle(JsonElement obj, string name)
    {
        JsonElement el = GetRequiredProperty(obj, name);
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
        {
            return (float)d;
        }
        if (el.ValueKind == JsonValueKind.String &&
            float.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            return parsed;
        }
        throw new JsonException($"JSON property '{name}' is not a valid Single.");
    }
}
