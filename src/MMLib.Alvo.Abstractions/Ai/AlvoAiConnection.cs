using System.Text.Json.Serialization;

namespace MMLib.Alvo.Ai;

/// <summary>Which protocol an AI endpoint speaks.</summary>
/// <remarks>
/// Two members, because two is what this build has adapters for. A third arrives with its adapter rather
/// than ahead of it — an enum member nothing can dial is a promise the product has not made.
/// </remarks>
public enum AiConnectionKind
{
    /// <summary>The OpenAI chat-completions shape: Ollama, vLLM, OpenAI itself, and everything that copies it.</summary>
    OpenAiCompatible = 0,

    /// <summary>Azure OpenAI, whose deployment-scoped routes and header differ from the shape above.</summary>
    AzureOpenAi = 1,
}

/// <summary>Where a resolved connection came from.</summary>
/// <remarks>
/// Reported on <c>GET {m}/info</c> so an operator can tell a value their deployment pinned from one somebody
/// saved in the dashboard — the question every "why is it still using the old model" starts with.
/// </remarks>
public enum AiConnectionSource
{
    /// <summary>Nothing is configured, so there is no connection and no agent.</summary>
    None = 0,

    /// <summary>The deployment's own configuration, which wins over anything stored.</summary>
    Configuration = 1,

    /// <summary>A record somebody saved, held in the secret store.</summary>
    Store = 2,
}

/// <summary>Whether a resolved connection has the key its endpoint needs.</summary>
/// <remarks>
/// <para>
/// <b>Reported because "configured" alone was a lie in the one case that matters</b> (docs/todo-admin.md §8d item
/// 31, 24 Sep 2026): <c>Alvo:Ai:ApiKeySecretRef</c> named a secret nobody had saved, <c>GET {m}/info</c> said
/// the connection was configured, and every turn was a 401 the operator had no way to see coming. Only the resolver
/// knows a reference went unanswered, so it is the resolver that says so.
/// </para>
/// <para>
/// <b>A state, never the key.</b> Nothing here says what the key is, how long it is or where it is kept — only
/// whether one will be sent.
/// </para>
/// <para>
/// <b>Serialised by name</b>, in the descriptor enums' camel case, so a client branches on <c>missing</c> rather
/// than on a number that means nothing on its own.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AiKeyState>))]
public enum AiKeyState
{
    /// <summary>There is no connection, so there is no key to speak of.</summary>
    [JsonStringEnumMemberName("none")]
    None = 0,

    /// <summary>A key resolved, and it is sent with every call.</summary>
    [JsonStringEnumMemberName("present")]
    Present = 1,

    /// <summary>
    /// A key is needed and none resolved, so every call is refused: the configured reference names a secret this
    /// instance does not have (or cannot read), or the endpoint is one that always needs a key and none was given.
    /// </summary>
    [JsonStringEnumMemberName("missing")]
    Missing = 2,

    /// <summary>
    /// No key was asked for and the endpoint is not one known to need one — a local Ollama or vLLM, run keyless on
    /// purpose.
    /// </summary>
    [JsonStringEnumMemberName("notNeeded")]
    NotNeeded = 3,
}

/// <summary>
/// One resolved AI connection: what to dial, as what, with which credential.
/// </summary>
/// <remarks>
/// <para>
/// <b>Infrastructure, never the descriptor.</b> A model name and an endpoint are properties of the
/// deployment, not of the backend being described — the same separation PLAN §4 keeps for a connection
/// string. A descriptor that named a model would be a descriptor that stopped applying when the operator
/// switched providers.
/// </para>
/// <para>
/// <b><see cref="ToString"/> is overridden, and that is a security control rather than a nicety.</b> The
/// record's generated <c>ToString</c> prints every member, so one interpolated log line, one exception
/// message or one debugger watch would put the API key into a log file — the exact leak §7.1 asks to be
/// impossible ("secrets never into logs, env dumps or git").
/// </para>
/// </remarks>
/// <param name="Kind">Which protocol the endpoint speaks.</param>
/// <param name="Endpoint">The base address to dial.</param>
/// <param name="Model">The model or deployment name to ask for.</param>
/// <param name="ApiKey">The credential, or <see langword="null"/> for an endpoint that needs none.</param>
public sealed record AlvoAiConnection(AiConnectionKind Kind, Uri Endpoint, string Model, string? ApiKey)
{
    /// <summary>The connection as it is safe to print: kind, host and model, never the key.</summary>
    /// <remarks>
    /// The host rather than the whole address, because a query string is a place credentials end up in
    /// practice however clearly the contract says otherwise.
    /// </remarks>
    public override string ToString() =>
        $"{Kind} {Endpoint?.Host ?? "?"} {Model}";
}
