namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>One value of a JSON document as <see cref="DescriptorJson"/> reads it: a value, so two reads of one text are equal.</summary>
internal abstract record JsonNode;

/// <summary>An object, its members in document order, duplicates kept; a lookup answers the last one, as a JSON reader that overwrites would.</summary>
internal sealed record JsonObject(EquatableArray<JsonMember> Members) : JsonNode
{
    internal JsonNode? Get(string key)
    {
        for (var i = Members.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
            {
                return Members[i].Value;
            }
        }

        return null;
    }

    internal string? String(string key) => Get(key) is JsonString text ? text.Value : null;

    internal bool? Bool(string key) => Get(key) is JsonBool flag ? flag.Value : null;

    internal JsonObject? Object(string key) => Get(key) as JsonObject;

    internal IEnumerable<string> Strings(string key) =>
        Get(key) is JsonArray array ? array.Items.OfType<JsonString>().Select(text => text.Value) : [];
}

/// <summary>One member of an object.</summary>
internal readonly record struct JsonMember(string Key, JsonNode Value);

/// <summary>An array, its items in order.</summary>
internal sealed record JsonArray(EquatableArray<JsonNode> Items) : JsonNode;

/// <summary>A string, unescaped.</summary>
internal sealed record JsonString(string Value) : JsonNode;

/// <summary>A number, as its raw text: the generator reads few numbers and never needs one as a double.</summary>
internal sealed record JsonNumber(string Raw) : JsonNode;

/// <summary><c>true</c> or <c>false</c>.</summary>
internal sealed record JsonBool(bool Value) : JsonNode;

/// <summary><c>null</c>.</summary>
internal sealed record JsonNull : JsonNode
{
    internal static JsonNull Instance { get; } = new();
}
