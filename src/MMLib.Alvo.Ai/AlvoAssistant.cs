using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Management;

using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

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
/// <b>A proposal is what a dry-run patch becomes.</b> The agent sends RFC 6902 operations, Alvo applies them to
/// the applied descriptor and dry-runs the result, and the agent has no way to apply — so the only path from its
/// draft to the database is <see cref="AssistantUpdate.Proposal"/> and the operator pressing the same
/// button they press for their own edits — carrying the revision the agent read, so two people composing at
/// once cannot overwrite each other silently.
/// </para>
/// <para>
/// <b>A turn is bounded twice.</b> The tools stop dry-running after three refusals that made no progress, or six in
/// all (D41); the loop itself ends after
/// <see cref="MaximumIterations"/> model round-trips, so a model that never stops calling tools ends as a turn
/// rather than a bill.
/// </para>
/// <para>
/// <b>The skills are read-only too</b> (D28): <c>load_skill</c> and <c>read_skill_resource</c> read fixed embedded
/// text.
/// </para>
/// <para>
/// <b>Nothing the operator typed is logged</b>, and neither is what the model wrote. A message to a schema assistant
/// routinely carries a connection string somebody pasted, and a log line is the place §7.1 asks a secret never to
/// reach. The trace (D44) records calls, not prose, and the log records where a patch wrote, never what: each call is
/// one 6202 line with its operations reduced to op and path and its violations to source, pointer, code and severity,
/// and the turn's end is one 6203 line.
/// </para>
/// <para>
/// <b>The trace is opt-in</b> (D45): <see cref="AssistantRequest.IncludeTrace"/> asks for it, and it is the turn's last
/// update, after the proposal or the failure. A turn that never reached an endpoint — no connection — has none.
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

        /* Disposed with the turn it belongs to, and the client with it (a DelegatingChatClient disposes its inner
           client). Today's OpenAI client holds nothing that needs releasing, but IChatClient is IDisposable and a
           factory that later supplies its own HttpClient would start leaking handlers silently. */
        using var recorder = new TurnRecorder(_clients(connection));
        var turn = new TurnState();
        try
        {
            await foreach (var update in AnswerAsync(recorder, request, connection, turn, ct).ConfigureAwait(false))
            {
                yield return update;
            }

            if (request.IncludeTrace)
            {
                yield return new AssistantUpdate.TurnTraced(TurnTrace.Json(TurnTrace.Of(HeaderOf(request, connection), recorder.Calls, turn.End)));
            }
        }
        finally
        {
            LogTurn(recorder, turn.End);
        }
    }

    /// <summary>
    /// The turn itself: the agent's stream translated, then the proposal — recording how it ended in
    /// <paramref name="turn"/>, which the trace and the 6203 line read.
    /// </summary>
    private async IAsyncEnumerable<AssistantUpdate> AnswerAsync(
        TurnRecorder recorder, AssistantRequest request, AlvoAiConnection connection, TurnState turn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tools = ManagementTools.For(_management, request.Project);
        var answer = new StringBuilder();
        var updates = RunAsync(recorder, tools, request, ct).GetAsyncEnumerator(ct);
        await using (updates.ConfigureAwait(false))
        {
            while (true)
            {
                var next = await NextAsync(updates, connection.Model).ConfigureAwait(false);
                if (next.Failure is { } failure)
                {
                    turn.End = TurnEnd.EndpointFailed;
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

        turn.End = recorder.ToolRounds >= MaximumIterations ? TurnEnd.IterationCap : TurnEnd.Answered;
        if (ProposalFrom(tools, answer) is { } proposal)
        {
            yield return proposal;
        }
    }

    /// <summary>Logs each call as 6202 — its log line, never its values — and the turn's end as 6203.</summary>
    /// <remarks>
    /// Built from the recorder's calls, not from the capped trace, so a call the trace dropped is still logged
    /// (pre-flight M2); and called from a <c>finally</c>, so an abandoned turn is logged too (L12).
    /// </remarks>
    private void LogTurn(TurnRecorder recorder, string end)
    {
        var calls = recorder.Calls;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            foreach (var call in calls)
            {
                var line = LogLineOf(call);
                CallTraced(_logger, call.Round, line);
            }
        }

        TurnEnded(_logger, end, calls.Count, recorder.ToolRounds);
    }

    /// <summary>One call's 6202 line: its scrubbed trace entry, reduced to what a log may carry.</summary>
    private static string LogLineOf(TracedCall call) =>
        TurnTrace.Json(TurnTrace.LogLine((JsonObject)SecretScrub.Scrub(TurnTrace.Entry(call))!));

    /// <summary>The trace's header: the instructions' version, the provider, the model and the project.</summary>
    private static TurnHeader HeaderOf(AssistantRequest request, AlvoAiConnection connection) => new(
        AssistantInstructions.VersionLine.Replace("<!-- ", string.Empty, StringComparison.Ordinal)
            .Replace(" -->", string.Empty, StringComparison.Ordinal),
        connection.Kind.ToString(), connection.Model, request.Project);

    /// <summary>How the turn ended — <see cref="TurnEnd.Abandoned"/> until it says otherwise.</summary>
    /// <remarks>A class, because an async iterator can take no <c>ref</c>: the answer sets it, the trace and the log read it.</remarks>
    private sealed class TurnState
    {
        internal string End { get; set; } = TurnEnd.Abandoned;
    }

    /// <summary>
    /// Advances the agent's stream, turning an endpoint that failed into something to say in the thread.
    /// </summary>
    /// <remarks>
    /// <b>A step of its own because <c>yield return</c> cannot live inside a <c>try</c> with a
    /// <c>catch</c>.</b> What is caught is deliberately broad: everything past this point is somebody else's
    /// endpoint — a wrong base address, an expired key, a model that does not exist, a socket that closed
    /// mid-stream — and every one of them is a sentence the operator needs rather than an exception the
    /// dashboard has to survive. <see cref="FailureMessage(Exception, string)"/> is what tells them apart; the
    /// exception is always logged, and the operator's own message never is.
    /// </remarks>
    /// <param name="updates">The agent's stream.</param>
    /// <param name="model">The model this turn asked for, in case the endpoint answered "no such model".</param>
    private async ValueTask<StreamStep> NextAsync(IAsyncEnumerator<AgentResponseUpdate> updates, string model)
    {
        try
        {
            return new StreamStep(await updates.MoveNextAsync().ConfigureAwait(false), Failure: null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            TurnFailed(_logger, failure);

            return new StreamStep(Moved: false, new AssistantUpdate.Failed(FailureMessage(failure, model)));
        }
    }

    /// <summary>
    /// What to tell the operator about a failed turn: which thing failed, when the endpoint's own answer
    /// said — and the old, generic sentence when it never answered at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pure and static</b>, deliberately: everything this decides is already in <paramref name="failure"/>
    /// and <paramref name="model"/> — never the connection's key or endpoint, and never the provider's own
    /// message, which routinely echoes the request back (the live case that prompted this: <c>HTTP 401
    /// (invalid_request_error: invalid_api_key)</c>) into a sentence an operator may screenshot.
    /// </para>
    /// <para>
    /// <b>Only <see cref="ClientResultException"/> is classified further.</b> It is what the OpenAI client
    /// throws for a response it received and did not like, so its <see cref="ClientResultException.Status"/>
    /// is the provider's own status code. Anything else reaching here — <see cref="HttpRequestException"/>, a
    /// timeout, a DNS failure — is a call that never got a response at all, which is exactly what
    /// <see cref="EndpointFailed"/> already said, and now correctly.
    /// </para>
    /// </remarks>
    /// <param name="failure">What the endpoint threw.</param>
    /// <param name="model">The model this turn asked for, already shown on Settings.</param>
    internal static string FailureMessage(Exception failure, string model) =>
        failure is ClientResultException client ? ClientFailureMessage(client, model) : EndpointFailed;

    /// <summary>What a provider that answered — with a status code — is telling the operator.</summary>
    private static string ClientFailureMessage(ClientResultException client, string model) => client.Status switch
    {
        401 or 403 => KeyRefused,
        404 => $"The AI provider has no model called '{model}' for this key.",
        429 => RateLimited,
        var status => $"The AI provider answered with an error ({status}).",
    };

    /// <summary>One step of the agent's stream: whether it moved, or what stopped it.</summary>
    private readonly record struct StreamStep(bool Moved, AssistantUpdate.Failed? Failure);

    /// <summary>Runs the agent loop for one turn.</summary>
    private static IAsyncEnumerable<AgentResponseUpdate> RunAsync(
        IChatClient client, ManagementTools tools, AssistantRequest request, CancellationToken ct) =>
        AgentFor(client, tools).RunStreamingAsync(Conversation(request), session: null, options: null, ct);

    /// <summary>
    /// The agent for one turn: the fixed instructions, the tools, the descriptor skills, and a capped, sequential invoker.
    /// </summary>
    internal static ChatClientAgent AgentFor(IChatClient client, ManagementTools tools)
    {
        var agent = new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = AgentName,
                ChatOptions = new ChatOptions { Instructions = AssistantInstructions.Text, Tools = [.. tools.Functions] },
                AIContextProviders = [EmbeddedSkills.Provider],
            },
            loggerFactory: null,
            services: null);
        Constrain(agent);

        return agent;
    }

    /// <summary>
    /// Caps the function-invoking loop the agent built for itself at <see cref="MaximumIterations"/>, and keeps it
    /// invoking one call at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent's own invoker, found rather than replaced, so the pipeline the agent builds — and whatever else it
    /// configures on that invoker — stays the one that runs. Sequential invocation is set rather than inherited
    /// because <see cref="ManagementTools"/>' budget and proposal are plain state that assumes it.
    /// </para>
    /// <para>
    /// <b>A missing invoker throws, deliberately</b>, and outside <see cref="NextAsync"/>'s catch, so it surfaces
    /// from <see cref="AskAsync"/> as the bug it is rather than as a sentence to the operator. Whether the agent
    /// inserts one is fixed per package version, not per endpoint, and the iteration-cap fact fails first.
    /// </para>
    /// </remarks>
    private static void Constrain(ChatClientAgent agent)
    {
        var invoker = agent.ChatClient.GetService<FunctionInvokingChatClient>()
            ?? throw new InvalidOperationException("The agent built no function-invoking client to cap.");
        invoker.MaximumIterationsPerRequest = MaximumIterations;
        invoker.AllowConcurrentInvocation = false;
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
    /// A turn proposes through <c>propose_change</c>, whose every draft has been through the dry run: the last
    /// valid proposal, else the last refused one. A turn that only answered a question — or only asked
    /// <c>check_change</c> — has none.
    /// </remarks>
    private static AssistantUpdate.Proposal? ProposalFrom(ManagementTools tools, StringBuilder answer) =>
        tools.Proposal is { } draft
            ? new AssistantUpdate.Proposal(draft.DescriptorJson, draft.ExpectedRevision, SummaryOf(answer, draft), draft.Refusals)
            : null;

    /// <summary>The turn's answer, or the proposal's own summary when the turn said nothing (D10).</summary>
    private static string SummaryOf(StringBuilder answer, ProposedDraft draft) =>
        answer.Length > 0 ? answer.ToString() : draft.Summary;

    /// <summary>What the agent calls itself, which some providers echo back in a response.</summary>
    private const string AgentName = "alvo-schema-assistant";

    /// <summary>How many model round-trips one turn may take before it ends as a turn rather than a bill.</summary>
    internal const int MaximumIterations = 12;

    /// <summary>What an operator is told when the endpoint itself failed.</summary>
    /// <remarks>
    /// Deliberately without the provider's own message. It routinely echoes the request — a base address, a
    /// header, sometimes the key — and this string is drawn in a conversation the operator may screenshot.
    /// The detail is in the host's log, where the exception went.
    /// </remarks>
    private const string EndpointFailed =
        "The AI endpoint did not answer. Check the connection under Settings — the address, the model name "
        + "and the key — and look at this instance's logs for what the provider said.";

    /// <summary>What an operator is told when the provider answered and refused the key (401 or 403).</summary>
    private const string KeyRefused =
        "The AI provider refused the key. Check the key the connection uses — under Settings, or "
        + "Alvo:Ai:ApiKeySecretRef and the secret it names — and this instance's log for the provider's answer.";

    /// <summary>What an operator is told when the provider is rate-limiting this key, or its quota ran out (429).</summary>
    private const string RateLimited =
        "The AI provider is rate-limiting this key, or its quota for it is exhausted. Wait and try again, or "
        + "check the provider's usage dashboard.";

    /// <summary>The failure an unconfigured deployment gets, in words that say what to do about it.</summary>
    private const string NoConnection =
        "No AI connection is configured for this instance, so there is nothing to ask. Set one under "
        + "Settings, or pin one in the deployment's own configuration under Alvo:Ai.";

    /// <remarks>
    /// No <c>{Tool}</c> placeholder: a tool name is the model's own text, and one it made up could carry a newline that
    /// forges a log line. The name is inside <c>{Call}</c>, scrubbed and JSON-escaped.
    /// </remarks>
    [LoggerMessage(EventId = 6202, Level = LogLevel.Information, Message = "Assistant call {Round}: {Call}")]
    private static partial void CallTraced(ILogger logger, int round, string call);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Information, Message = "Assistant turn ended ({End}) after {Calls} calls in {Rounds} tool rounds.")]
    private static partial void TurnEnded(ILogger logger, string end, int calls, int rounds);

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Warning,
        Message = "An assistant turn failed against the configured AI endpoint.")]
    private static partial void TurnFailed(ILogger logger, Exception failure);
}
