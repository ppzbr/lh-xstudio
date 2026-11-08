using System.Text.Json.Nodes;

namespace VocalPatch.Core.Formats;

/// <summary>Tolerant helpers for reading loosely-typed JSON interchange payloads.</summary>
internal static class JsonUtil
{
    public static JsonObject? Obj(JsonNode? n) => n as JsonObject;

    public static JsonArray? Arr(JsonNode? n) => n as JsonArray;

    public static double D(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, out var p)) return p;
        }
        return 0;
    }

    public static int I(JsonNode? n) => (int)Math.Round(D(n));

    public static bool B(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string S(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
}
