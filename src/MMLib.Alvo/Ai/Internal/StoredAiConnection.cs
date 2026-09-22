using System.Text.Json.Serialization;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// The AI connection as the secret store holds it — one JSON document under one secret name.
/// </summary>
/// <remarks>
/// <b>One secret rather than four.</b> The endpoint, the model and the key change together, and a save that
/// updated three names would leave a window in which the screen reports a model that is being dialled at the
/// previous endpoint. It also puts the whole record behind the store's encryption instead of only the key.
/// </remarks>
/// <param name="Kind">The wire spelling of <see cref="AiConnectionKind"/>.</param>
/// <param name="Endpoint">The base address.</param>
/// <param name="Model">The model or deployment name.</param>
/// <param name="ApiKey">The credential, or <see langword="null"/> for an endpoint that needs none.</param>
internal sealed record StoredAiConnection(
    string? Kind,
    string? Endpoint,
    string? Model,
    string? ApiKey);

/// <summary>The source-generated reader for <see cref="StoredAiConnection"/>.</summary>
/// <remarks>
/// Source-generated for the reason every other JSON in this repository is: reflection-based serialization is
/// what an AOT-published or trimmed host loses first, and a resolver that silently stopped parsing would
/// report "not configured" to an operator who had configured it.
/// </remarks>
[JsonSerializable(typeof(StoredAiConnection))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class StoredAiConnectionJsonContext : JsonSerializerContext;
