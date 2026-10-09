namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The five places a lone UTF-16 surrogate can hide in a descriptor. The JSON escape parses, then throws from Corvus
/// or System.Text.Json deep inside validation, which used to be an HTTP 500 on every management route.
/// </summary>
internal static class LoneSurrogateInputs
{
    /// <summary>Where the escape goes: an entity key, a field key, a role, a description, an <c>x-</c> extension.</summary>
    /// <returns>One row per place.</returns>
    public static TheoryData<string> Places() => ["entity-key", "field-key", "role", "description", "extension"];

    /// <summary>The compact descriptor JSON with a lone surrogate escape at <paramref name="where"/>.</summary>
    /// <param name="compactJson">The descriptor, serialised without whitespace.</param>
    /// <param name="where">One of <see cref="Places"/>.</param>
    /// <returns>The descriptor text carrying the raw <c>\ud800</c>-style escape.</returns>
    public static string At(string compactJson, string where) => where switch
    {
        "entity-key" => compactJson.Replace("\"entities\":{", "\"entities\":{\"\\ud800\":{\"fields\":{\"a\":{\"type\":\"string\"}}},", StringComparison.Ordinal),
        "field-key" => compactJson.Replace("\"fields\":{", "\"fields\":{\"\\ud800\":{\"type\":\"string\"},", StringComparison.Ordinal),
        "role" => compactJson.Replace("\"roles\":[", "\"roles\":[\"\\ud800\",", StringComparison.Ordinal),
        "description" => compactJson.Replace("\"description\":\"", "\"description\":\"\\udc00", StringComparison.Ordinal),
        _ => compactJson.Replace("\"entities\":{", "\"x-a\":\"\\ud800\",\"entities\":{", StringComparison.Ordinal),
    };
}
