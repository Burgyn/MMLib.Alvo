using MMLib.Alvo.Ai;
using MMLib.Alvo.Management;
using MMLib.Alvo.Secrets;

using System.Text.Json;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// What the dashboard knows about the assistant: whether there is one, what to ask it, and how to save the
/// connection it dials.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both dependencies are optional, and neither absence is an error.</b> A deployment that installed no
/// agent package has no <see cref="IAlvoAssistant"/>; one whose secrets are deployed rather than clicked has
/// no writable <see cref="ISecretStore"/>. The screens ask before they draw a control, which is the same
/// shape <c>IAlvoUserAdministration</c> already has in this dashboard.
/// </para>
/// <para>
/// <b>It saves a connection and never reads one back.</b> The settings panel writes a key; the value that
/// comes back out is the redacted summary <c>GET {m}/info</c> reports. A panel that pre-filled the stored
/// key would be a panel that puts it in the page source of every browser that opens it.
/// </para>
/// </remarks>
/// <param name="assistant">The agent, or <see langword="null"/> when none is installed.</param>
/// <param name="secrets">The secret store, or <see langword="null"/> when none is registered.</param>
/// <param name="management">Where "is an AI configured" is answered, so there is one authority on it.</param>
internal sealed class AssistantGateway(
    IAlvoAssistant? assistant, ISecretStore? secrets, ManagementGateway management)
{
    /// <summary>Whether this deployment can save a connection from the dashboard at all.</summary>
    /// <remarks>
    /// False for every GitOps deployment, which is the common case rather than the broken one: its secrets
    /// come from a file or a vault, and the screen says so instead of offering a save that would fail.
    /// </remarks>
    public bool CanWriteConnection => secrets is { CanWrite: true };

    /// <summary>Whether an agent is installed in this process.</summary>
    public bool IsInstalled => assistant is not null;

    /// <summary>
    /// What this instance reports about its AI connection.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="ManagementGateway"/> rather than resolved here, so the dashboard and every
    /// other client of <c>GET {m}/info</c> answer the question from one place.
    /// </remarks>
    /// <param name="ct">A token to cancel the read.</param>
    public async ValueTask<ManagementAi> ConnectionAsync(CancellationToken ct) =>
        (await management.InfoAsync(ct).ConfigureAwait(false)).Ai;

    /// <summary>Whether a turn can be asked at all: an agent is installed and a connection resolves.</summary>
    /// <param name="ct">A token to cancel the read.</param>
    public async ValueTask<bool> IsConfiguredAsync(CancellationToken ct) =>
        IsInstalled && (await ConnectionAsync(ct).ConfigureAwait(false)).Configured;

    /// <summary>
    /// Asks one turn.
    /// </summary>
    /// <param name="request">The operator's message and the conversation it belongs to.</param>
    /// <param name="ct">A token to cancel the turn.</param>
    /// <returns>The updates, in the order they happened.</returns>
    /// <remarks>
    /// With no agent installed the answer is a single failure rather than an exception — the caller is
    /// drawing a conversation, and a screen that had to catch to say "there is no assistant here" would
    /// eventually draw a stack trace instead.
    /// </remarks>
    public IAsyncEnumerable<AssistantUpdate> AskAsync(AssistantRequest request, CancellationToken ct) =>
        assistant is null ? NotInstalled() : assistant.AskAsync(request, ct);

    /// <summary>
    /// Saves the connection as one secret, replacing whatever was there.
    /// </summary>
    /// <param name="form">What the operator typed.</param>
    /// <param name="ct">A token to cancel the write.</param>
    /// <exception cref="InvalidOperationException">This deployment cannot save a connection.</exception>
    /// <remarks>
    /// <b>One name, and no other.</b> The whole record goes under
    /// <see cref="StoredAiConnection.SecretName"/>, so the endpoint, the model and the key are replaced
    /// together and there is no window in which the screen reports one and the agent dials another.
    /// </remarks>
    public async Task SaveConnectionAsync(AiConnectionForm form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (secrets is not { CanWrite: true } store)
        {
            throw new InvalidOperationException(NoWritableStore);
        }

        var record = new StoredAiConnection(form.Kind, form.Endpoint, form.Model, Blank(form.ApiKey));
        var json = JsonSerializer.Serialize(record, StoredAiConnectionJsonContext.Default.StoredAiConnection);

        await store.SetAsync(SecretName.Parse(StoredAiConnection.SecretName), json, ct).ConfigureAwait(false);
        management.Invalidate();
    }

    /// <summary>An empty key is no key, not an empty one.</summary>
    /// <remarks>
    /// A local Ollama needs none, and storing <c>""</c> would send an empty header that some gateways refuse
    /// differently from a missing one.
    /// </remarks>
    private static string? Blank(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;

    /// <summary>The one update a deployment with no agent package gets.</summary>
    private static async IAsyncEnumerable<AssistantUpdate> NotInstalled()
    {
        yield return new AssistantUpdate.Failed(NoAgentInstalled);

        await Task.CompletedTask;
    }

    private const string NoAgentInstalled =
        "This instance has no assistant installed. The agent ships as MMLib.Alvo.Ai, which a host adds with "
        + "AddAlvoAi() — the standalone image already has it.";

    private const string NoWritableStore =
        "This deployment's secrets come from its own configuration, which Alvo reads and never writes. Set "
        + "the connection where that configuration comes from, under Alvo:Ai.";
}

/// <summary>What the settings panel collected.</summary>
/// <param name="Kind">The wire spelling of the protocol.</param>
/// <param name="Endpoint">The base address to dial.</param>
/// <param name="Model">The model or deployment name.</param>
/// <param name="ApiKey">The credential, or empty for an endpoint that needs none.</param>
internal sealed record AiConnectionForm(string Kind, string Endpoint, string Model, string? ApiKey);
