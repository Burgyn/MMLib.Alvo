using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Exchanges;

internal sealed record ExchangeKey(IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes, Guid? Tenant, string SecretVariable)
{
    internal const string DefaultSecretVariable = "ALVO_KEY_SECRET";
}

internal sealed record ExchangeStep(
    string? Key,
    string Method,
    string Path,
    JsonNode? Body,
    string? BodyFile,
    string? BodyFileAs,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    int Expect,
    string? ExpectType,
    IReadOnlyList<string> ShowHeaders);

internal sealed record ExchangeSpec(string Name, string Descriptor, IReadOnlyDictionary<string, ExchangeKey> Keys, IReadOnlyList<ExchangeStep> Steps)
{
    private static readonly string[] _specMembers = ["descriptor", "keys", "steps"];
    private static readonly (string Header, string Member)[] _ownedHeaders = [("X-Alvo-Api-Key", "key"), ("Content-Type", "contentType")];
    private static readonly string[] _keyMembers = ["roles", "scopes", "tenant", "secretVariable"];
    private static readonly string[] _stepMembers =
        ["key", "method", "path", "body", "bodyFile", "bodyFileAs", "headers", "contentType", "expect", "expectType", "showHeaders"];

    internal static ExchangeSpec Parse(string name, string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException($"{name}: the spec must be a JSON object.");
        var reader = new SpecReader(name);
        reader.RefuseUnknown(root, _specMembers, "the spec");
        var keys = reader.Required(root, "keys").AsObject()
            .ToDictionary(pair => pair.Key, pair => reader.Key(pair.Key, pair.Value), StringComparer.Ordinal);
        var steps = reader.Required(root, "steps").AsArray().Select((node, i) => reader.Step(i, node, keys)).ToList();
        return new ExchangeSpec(name, reader.String(root, "descriptor", "the spec"), keys, steps);
    }

    private sealed class SpecReader(string name)
    {
        internal ExchangeKey Key(string id, JsonNode? node)
        {
            var key = node as JsonObject ?? throw Fail($"key '{id}' must be an object");
            RefuseUnknown(key, _keyMembers, $"key '{id}'");
            return new ExchangeKey(
                Strings(key["roles"]),
                Strings(key["scopes"]),
                key["tenant"] is { } tenant ? Guid.Parse(tenant.GetValue<string>()) : null,
                key["secretVariable"]?.GetValue<string>() ?? ExchangeKey.DefaultSecretVariable);
        }

        internal ExchangeStep Step(int index, JsonNode? node, Dictionary<string, ExchangeKey> keys)
        {
            var where = $"step {index}";
            var step = node as JsonObject ?? throw Fail($"{where} must be an object");
            RefuseUnknown(step, _stepMembers, where);
            var key = step["key"]?.GetValue<string>();
            if (key is not null && !keys.ContainsKey(key))
            {
                throw Fail($"{where} uses key '{key}', which 'keys' does not declare");
            }

            var headers = Headers(step["headers"]);
            RefuseOwnedHeaders(headers, where);
            RefuseContentTypeWithoutBody(step, where);
            RefuseBodyFileAsWithoutBodyFile(step, where);
            return new ExchangeStep(
                key, String(step, "method", where).ToUpperInvariant(), String(step, "path", where),
                step["body"]?.DeepClone(), step["bodyFile"]?.GetValue<string>(), step["bodyFileAs"]?.GetValue<string>(), headers,
                step["contentType"]?.GetValue<string>(), Required(step, "expect", where).GetValue<int>(),
                step["expectType"]?.GetValue<string>(), Strings(step["showHeaders"]));
        }

        private void RefuseOwnedHeaders(Dictionary<string, string> headers, string where)
        {
            foreach (var (header, member) in _ownedHeaders)
            {
                if (headers.Keys.Any(name => string.Equals(name, header, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Fail($"{where} sets '{header}' in 'headers'; use '{member}' instead");
                }
            }
        }

        private void RefuseContentTypeWithoutBody(JsonObject step, string where)
        {
            if (step["contentType"] is not null && step["body"] is null && step["bodyFile"] is null)
            {
                throw Fail($"{where} sets 'contentType' but sends no 'body' or 'bodyFile'");
            }
        }

        private void RefuseBodyFileAsWithoutBodyFile(JsonObject step, string where)
        {
            if (step["bodyFileAs"] is not null && step["bodyFile"] is null)
            {
                throw Fail($"{where} sets 'bodyFileAs' but names no 'bodyFile'");
            }
        }

        internal void RefuseUnknown(JsonObject node, string[] known, string where)
        {
            var unknown = node.Select(pair => pair.Key).FirstOrDefault(member => !known.Contains(member, StringComparer.Ordinal));
            if (unknown is not null)
            {
                throw new InvalidOperationException($"{name}: unknown member '{unknown}' in {where}");
            }
        }

        internal JsonNode Required(JsonObject node, string member, string where = "the spec") =>
            node[member] ?? throw Fail($"{where} is missing the required member '{member}'");

        internal string String(JsonObject node, string member, string where) => Required(node, member, where).GetValue<string>();

        private static List<string> Strings(JsonNode? node) =>
            node is JsonArray array ? [.. array.Select(item => item!.GetValue<string>())] : [];

        private static Dictionary<string, string> Headers(JsonNode? node) =>
            node is JsonObject headers
                ? headers.ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

        private InvalidOperationException Fail(string message) => new($"{name}: {message}");
    }
}
