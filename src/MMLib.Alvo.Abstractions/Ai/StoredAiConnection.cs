using System.Text.Json.Serialization;

namespace MMLib.Alvo.Ai;

/// <summary>
/// The AI connection as the secret store holds it — one JSON document under one secret name.
/// </summary>
/// <remarks>
/// <para>
/// <b>One secret rather than four.</b> The endpoint, the model and the key change together, and a save that
/// updated three names would leave a window in which the screen reports a model being dialled at the
/// previous endpoint. It also puts the whole record behind the store's encryption instead of only the key.
/// </para>
/// <para>
/// <b>Public, and in Abstractions, because two sides write and read it.</b> The core's resolver parses it
/// and the dashboard's settings screen writes it, and they cannot see each other — so a private shape in
/// either would be two spellings of one document, and the day one gained a field the other would silently
/// stop reading it.
/// </para>
/// </remarks>
/// <param name="Kind">The wire spelling of <see cref="AiConnectionKind"/>.</param>
/// <param name="Endpoint">The base address.</param>
/// <param name="Model">The model or deployment name.</param>
/// <param name="ApiKey">The credential, or <see langword="null"/> for an endpoint that needs none.</param>
public sealed record StoredAiConnection(string? Kind, string? Endpoint, string? Model, string? ApiKey)
{
    /// <summary>
    /// The secret name this document is held under.
    /// </summary>
    /// <remarks>
    /// Namespaced under <c>alvo.</c> so a deployment's own secrets cannot collide with the framework's, the
    /// way every other reserved name in this repository is.
    /// </remarks>
    public const string SecretName = "alvo.ai.connection";

    /// <summary>The wire spelling of <see cref="AiConnectionKind.OpenAiCompatible"/>.</summary>
    public const string OpenAiCompatibleKind = "openai-compatible";

    /// <summary>The wire spelling of <see cref="AiConnectionKind.AzureOpenAi"/>.</summary>
    public const string AzureOpenAiKind = "azure-openai";
}

/// <summary>The source-generated reader and writer for <see cref="StoredAiConnection"/>.</summary>
/// <remarks>
/// Source-generated for the reason every other JSON in this repository is: reflection-based serialization is
/// what an AOT-published or trimmed host loses first, and a resolver that silently stopped parsing would
/// report "not configured" to an operator who had configured it.
/// </remarks>
[JsonSerializable(typeof(StoredAiConnection))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class StoredAiConnectionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
