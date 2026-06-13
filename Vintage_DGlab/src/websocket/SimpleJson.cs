using System.Text.RegularExpressions;

namespace VintageDLab.WebSocket;

/// <summary>
/// Minimal JSON parser for the DG-Lab protocol messages.
/// Avoids pulling in a full JSON library dependency.
/// </summary>
internal static class SimpleJson
{
    public static SimpleJsonObject? Parse(string json)
    {
        var result = new SimpleJsonObject();
        // Match "key":"value" pairs (handles escaped chars in values)
        var matches = Regex.Matches(json, "\"(\\w+)\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        foreach (Match m in matches)
        {
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\/", "/");
            result.Put(key, value);
        }
        return result;
    }
}

internal class SimpleJsonObject
{
    private readonly Dictionary<string, string> _values = new();

    public void Put(string key, string value) => _values[key] = value;

    public bool TryGetValue(string key, out string value)
    {
        return _values.TryGetValue(key, out value!);
    }
}
