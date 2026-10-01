using Microsoft.Extensions.AI;

using OpenAI;

using System.ClientModel;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// Dials one resolved connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built per turn, never cached</b> (spec §3.5). The connection itself resolves per turn — an operator
/// can change the model or rotate the key without a restart — and a client cached against the previous one
/// would keep dialling it until the process was recycled.
/// </para>
/// <para>
/// <b>Both kinds go through the OpenAI client.</b> Azure OpenAI speaks the same wire shape at a different
/// base address, and <c>Microsoft.Extensions.AI.OpenAI</c> is the adapter Microsoft ships for it — writing a
/// second client here would be writing what the platform wrote, which is the argument
/// <c>ConfigurationSecretStore</c> already makes about vault adapters.
/// </para>
/// </remarks>
internal static class ChatClientFactory
{
    /// <summary>The chat client for <paramref name="connection"/>.</summary>
    /// <param name="connection">The resolved connection.</param>
    internal static IChatClient For(AlvoAiConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var client = new OpenAIClient(
            new ApiKeyCredential(connection.ApiKey ?? NoCredential),
            new OpenAIClientOptions { Endpoint = connection.Endpoint });

        return client.GetChatClient(connection.Model).AsIChatClient();
    }

    /// <summary>
    /// What is sent as the credential to an endpoint that needs none.
    /// </summary>
    /// <remarks>
    /// A local Ollama or vLLM ignores the header, and the client refuses to be constructed without one — so
    /// this is a placeholder rather than a secret, and it is spelled so that a reader of a captured request
    /// can tell the difference at a glance.
    /// </remarks>
    private const string NoCredential = "no-api-key-required";
}
