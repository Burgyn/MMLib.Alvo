using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Management;

using System.Runtime.CompilerServices;
using System.Text;

namespace MMLib.Alvo.Ai;

/// <summary>
/// The one <see cref="IAlvoAssistant"/>: a Microsoft Agent Framework loop over a tool set that cannot write.
/// </summary>
/// <remarks>
/// <para>
/// <b>The connection resolves per turn, and so does the client.</b> An operator can change the model or
/// rotate the key from the dashboard without a restart; an assistant that captured either at construction
/// would keep using the old one until the process was recycled.
/// </para>
/// <para>
/// <b>A proposal is what a validated draft becomes.</b> The agent has no way to apply, so the only path from
/// its draft to the database is <see cref="AssistantUpdate.Proposal"/> and the operator pressing the same
/// button they press for their own edits — carrying the revision the agent read, so two people composing at
/// once cannot overwrite each other silently.
/// </para>
/// <para>
/// <b>Nothing the operator typed is logged.</b> A message to a schema assistant routinely carries a
/// connection string somebody pasted, and a log line is the place §7.1 asks a secret never to reach.
/// </para>
/// </remarks>
public sealed partial class AlvoAssistant : IAlvoAssistant
{
    private readonly IAlvoManagement _management;
    private readonly IAiConnectionResolver _connections;
    private readonly Func<AlvoAiConnection, IChatClient> _clients;
    private readonly ILogger<AlvoAssistant> _logger;

    /// <summary>Initializes the assistant.</summary>
    /// <param name="management">The Management API the tools read through.</param>
    /// <param name="connections">Resolves the connection to dial, per turn.</param>
    /// <param name="clients">
    /// Builds the chat client for a connection. A delegate rather than a concrete factory, so a test can
    /// script the model without a network — every behaviour this class owns is decided before a token is
    /// sent, and none of it should need an endpoint to measure.
    /// </param>
    /// <param name="logger">Where a failed turn is reported, without the operator's message in it.</param>
    internal AlvoAssistant(
        IAlvoManagement management,
        IAiConnectionResolver connections,
        Func<AlvoAiConnection, IChatClient> clients,
        ILogger<AlvoAssistant> logger)
    {
        ArgumentNullException.ThrowIfNull(management);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(logger);

        _management = management;
        _connections = connections;
        _clients = clients;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AssistantUpdate> AskAsync(
        AssistantRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((await _connections.ResolveAsync(ct).ConfigureAwait(false)).Connection is not { } connection)
        {
            yield return new AssistantUpdate.Failed(NoConnection);
            yield break;
        }

        var tools = ManagementTools.For(_management, request.Project);
        var answer = new StringBuilder();

        /* Disposed with the turn it belongs to. Today's OpenAI client holds nothing that needs releasing,
           but IChatClient is IDisposable and a factory that later supplies its own HttpClient would start
           leaking handlers silently. */
        using var client = _clients(connection);

        var updates = RunAsync(client, tools, request, ct).GetAsyncEnumerator(ct);
        await using (updates.ConfigureAwait(false))
        {
            while (true)
            {
                var next = await NextAsync(updates).ConfigureAwait(false);
                if (next.Failure is { } failure)
                {
                    yield return failure;
                    yield break;
                }

                if (!next.Moved)
                {
                    break;
                }

                foreach (var translated in Translate(updates.Current, answer))
                {
                    yield return translated;
                }
            }
        }

        if (ProposalFrom(tools, answer) is { } proposal)
        {
            yield return proposal;
        }
    }

    /// <summary>
    /// Advances the agent's stream, turning an endpoint that failed into something to say in the thread.
    /// </summary>
    /// <remarks>
    /// <b>A step of its own because <c>yield return</c> cannot live inside a <c>try</c> with a
    /// <c>catch</c>.</b> What is caught is deliberately broad: everything past this point is somebody else's
    /// endpoint — a wrong base address, an expired key, a model that does not exist, a socket that closed
    /// mid-stream — and every one of them is a sentence the operator needs rather than an exception the
    /// dashboard has to survive. The exception is logged; the operator's message never is.
    /// </remarks>
    private async ValueTask<StreamStep> NextAsync(IAsyncEnumerator<AgentResponseUpdate> updates)
    {
        try
        {
            return new StreamStep(await updates.MoveNextAsync().ConfigureAwait(false), Failure: null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            TurnFailed(_logger, failure);

            return new StreamStep(Moved: false, new AssistantUpdate.Failed(EndpointFailed));
        }
    }

    /// <summary>One step of the agent's stream: whether it moved, or what stopped it.</summary>
    private readonly record struct StreamStep(bool Moved, AssistantUpdate.Failed? Failure);

    /// <summary>Runs the agent loop for one turn.</summary>
    private static IAsyncEnumerable<AgentResponseUpdate> RunAsync(
        IChatClient client, ManagementTools tools, AssistantRequest request, CancellationToken ct)
    {
        var agent = new ChatClientAgent(
            client,
            instructions: SystemPrompt.Text,
            name: AgentName,
            description: null,
            tools: [.. tools.Functions]);

        return agent.RunStreamingAsync(Conversation(request), session: null, options: null, ct);
    }

    /// <summary>The conversation as the model sees it: the caller's history, then this turn's message.</summary>
    /// <remarks>
    /// The caller's history rather than a session the assistant holds: a dashboard that drew a conversation
    /// is the one authority on what that conversation was, and a session here would outlive the screen.
    /// </remarks>
    private static IEnumerable<ChatMessage> Conversation(AssistantRequest request)
    {
        foreach (var turn in request.History)
        {
            yield return new ChatMessage(
                turn.Role is AssistantRole.Assistant ? ChatRole.Assistant : ChatRole.User, turn.Text);
        }

        yield return new ChatMessage(ChatRole.User, request.Message);
    }

    /// <summary>One framework update as the updates a caller sees, accumulating the answer as it goes.</summary>
    private static IEnumerable<AssistantUpdate> Translate(AgentResponseUpdate update, StringBuilder answer)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    yield return new AssistantUpdate.ToolInvoked(call.Name);
                    break;
                case TextContent { Text.Length: > 0 } text:
                    answer.Append(text.Text);
                    yield return new AssistantUpdate.Text(text.Text);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// The proposal this turn produced, or <see langword="null"/> when it produced none.
    /// </summary>
    /// <remarks>
    /// A turn proposes by validating: the agent cannot present a draft it has not run through the dry run,
    /// so the last validated draft is the proposal and a turn that only answered a question has none.
    /// </remarks>
    private static AssistantUpdate.Proposal? ProposalFrom(ManagementTools tools, StringBuilder answer) =>
        tools.LastValidated is { } draft
            ? new AssistantUpdate.Proposal(
                draft.DescriptorJson, draft.ExpectedRevision, answer.ToString(), draft.Refusals)
            : null;

    /// <summary>What the agent calls itself, which some providers echo back in a response.</summary>
    private const string AgentName = "alvo-schema-assistant";

    /// <summary>What an operator is told when the endpoint itself failed.</summary>
    /// <remarks>
    /// Deliberately without the provider's own message. It routinely echoes the request — a base address, a
    /// header, sometimes the key — and this string is drawn in a conversation the operator may screenshot.
    /// The detail is in the host's log, where the exception went.
    /// </remarks>
    private const string EndpointFailed =
        "The AI endpoint did not answer. Check the connection under Settings — the address, the model name "
        + "and the key — and look at this instance's logs for what the provider said.";

    /// <summary>The failure an unconfigured deployment gets, in words that say what to do about it.</summary>
    private const string NoConnection =
        "No AI connection is configured for this instance, so there is nothing to ask. Set one under "
        + "Settings, or pin one in the deployment's own configuration under Alvo:Ai.";

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Warning,
        Message = "An assistant turn failed against the configured AI endpoint.")]
    private static partial void TurnFailed(ILogger logger, Exception failure);
}
