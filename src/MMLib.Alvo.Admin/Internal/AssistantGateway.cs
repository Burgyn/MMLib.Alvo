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
    /// <summary>
    /// Raised when the stored connection changed, so a screen holding an answer from before it can ask again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists because "is an assistant configured" is answered once per circuit and then acted on for
    /// the rest of it.</b> <c>AdminLayout</c> mounts the drawer from a single <see cref="IsConfiguredAsync"/>
    /// in <c>OnInitializedAsync</c> — correct for a shell, wrong the moment the operator configures the
    /// connection in the same session, which left them reading a Settings page that said <c>configured</c>
    /// beside a shell with no launcher anywhere, and no reason to suspect a reload would fix it.
    /// </para>
    /// <para>
    /// <b>An event on this gateway rather than a poll or a shared flag.</b> The gateway is scoped, so it is
    /// already the one thing the layout and the settings page share for a circuit, and the write path that
    /// invalidates the cached <c>info</c> is the exact place that knows the answer may have moved. A layout
    /// that polled would ask on a timer for a change that happens at most once a session.
    /// </para>
    /// </remarks>
    public event Action? ConnectionChanged;

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
    /// <para>
    /// With no agent installed the answer is a single failure rather than an exception — the caller is
    /// drawing a conversation, and a screen that had to catch to say "there is no assistant here" would
    /// eventually draw a stack trace instead.
    /// </para>
    /// <para>
    /// <b>The whole turn runs with the operator published.</b> The agent reads the project through
    /// <c>IAlvoManagement</c>, which admits nobody it cannot see — so a turn run without the publication
    /// would have every tool answer <c>forbidden</c>. It is published around the enumeration rather than
    /// around a call, because the tools run between one update and the next.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<AssistantUpdate> AskAsync(AssistantRequest request, CancellationToken ct) =>
        assistant is null
            ? NotInstalled()
            : management.AsOperatorAsync(() => assistant.AskAsync(request, ct), ct);

    /// <summary>
    /// Saves the connection as one secret, replacing whatever was there.
    /// </summary>
    /// <param name="form">What the operator typed.</param>
    /// <param name="ct">A token to cancel the write.</param>
    /// <exception cref="InvalidOperationException">This deployment cannot save a connection.</exception>
    /// <remarks>
    /// <para>
    /// <b>Through the Management API, not the store.</b> Writing a credential is an administrator's
    /// operation, and the core is what decides who is one — a gateway that reached the store directly would
    /// let any signed-in operator repoint the assistant's endpoint at an address that then receives the
    /// descriptor, the schema and their colleagues' prompts.
    /// </para>
    /// <para>
    /// <b>One name, and no other.</b> The whole record goes under
    /// <see cref="StoredAiConnection.SecretName"/>, so the endpoint, the model and the key are replaced
    /// together and there is no window in which the screen reports one and the agent dials another.
    /// </para>
    /// </remarks>
    public async Task SaveConnectionAsync(AiConnectionForm form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!CanWriteConnection)
        {
            throw new SecretStoreReadOnlyException(NoWritableStore);
        }

        await management.SetAiConnectionAsync(
            new StoredAiConnection(form.Kind, form.Endpoint, form.Model, Blank(form.ApiKey)), ct)
            .ConfigureAwait(false);

        management.Invalidate();
        ConnectionChanged?.Invoke();
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
