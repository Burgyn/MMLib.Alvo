using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// Sits under the assistant's function-invoking loop and records each tool call and the answer it got (D44).
/// </summary>
/// <remarks>
/// <para>
/// <b>The eval's recording rule, re-stated rather than shared</b> (D44: the shape is reused, not the class; the rule's
/// other home is <c>eval/MMLib.Alvo.Ai.Eval/RecordingChatClient.cs</c>). A call is keyed by its round and its id, since
/// some OpenAI-compatible servers number call ids per response; a tool's answer is matched to the latest unanswered
/// call with its id, the one the request carrying it follows. Tokens and requests are the eval's concern and left out.
/// </para>
/// <para>
/// <b>Only calls are recorded</b> — never the messages, the history or the model's text — and nothing here is logged:
/// <see cref="TurnTrace"/> decides what of a call is kept.
/// </para>
/// </remarks>
/// <param name="inner">The chat client the turn dials.</param>
internal sealed class TurnRecorder(IChatClient inner) : DelegatingChatClient(inner)
{
    private static readonly HashSet<string> _dryRuns = new(StringComparer.Ordinal) { "check_change", "propose_change" };

    private readonly List<TracedCall> _calls = [];
    private int _requests;

    /// <summary>How many of the model's answers asked for at least one tool: what the iteration cap bounds.</summary>
    internal int ToolRounds { get; private set; }

    /// <summary>How many requests the turn has sent so far: the round its latest answer is recorded under.</summary>
    internal int Requests => _requests;

    /// <summary>
    /// The skills the turn loaded before its current dry run (D50): the <c>skillName</c> of every <c>load_skill</c> call
    /// whose answer is not an error, made before the first dry run not yet answered — or all of them when none is.
    /// </summary>
    /// <remarks>
    /// "Before" is by position in the calls (pre-flight L1): a skill loaded later in the same answer as the dry run had
    /// not been read when the dry run was checked. A load in the same answer and earlier is counted, and its answer is
    /// still null then, because the sequential invoker runs it first.
    /// </remarks>
    internal IReadOnlySet<string> LoadedSkills
    {
        get
        {
            var current = _calls.FindIndex(call => call.Result is null && _dryRuns.Contains(call.Tool));
            return (current < 0 ? _calls : _calls.Take(current))
                .Where(call => call.Tool == AgentSkillsProvider.LoadSkillToolName
                    && call.Result?.StartsWith("Error:", StringComparison.Ordinal) != true)
                .Select(call => SkillOf(call.Arguments))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Every tool call, in the order the model made them.</summary>
    internal IReadOnlyList<TracedCall> Calls => [.. _calls];

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = Sent(messages);
        var response = await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
        Recorded(response.Messages.SelectMany(message => message.Contents));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sent = Sent(messages);
        var received = new List<AIContent>();
        await foreach (var update in base.GetStreamingResponseAsync(sent, options, cancellationToken).ConfigureAwait(false))
        {
            received.AddRange(update.Contents);
            yield return update;
        }

        Recorded(received);
    }

    private List<ChatMessage> Sent(IEnumerable<ChatMessage> messages)
    {
        var sent = messages.ToList();
        _requests++;
        foreach (var result in sent.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
        {
            var index = _calls.FindLastIndex(call => call.CallId == result.CallId && call.Result is null);
            if (index >= 0)
            {
                _calls[index] = _calls[index] with { Result = TextOf(result.Result) };
            }
        }

        return sent;
    }

    private void Recorded(IEnumerable<AIContent> contents)
    {
        var round = _requests;
        var askedForTool = false;
        foreach (var call in contents.OfType<FunctionCallContent>())
        {
            askedForTool = true;
            _calls.Add(new TracedCall(round, call.CallId, call.Name, call.Arguments, Result: null));
        }

        ToolRounds += askedForTool ? 1 : 0;
    }

    private static string? SkillOf(IDictionary<string, object?>? arguments) =>
        arguments?.TryGetValue("skillName", out var value) == true
            ? value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value?.ToString()
            : null;

    private static string? TextOf(object? result) => result switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
        _ => result.ToString(),
    };
}

/// <summary>One tool call the model made, and what the tool answered — the eval's <c>RecordedCall</c> members.</summary>
/// <param name="Round">The request (1-based) whose answer asked for it.</param>
/// <param name="CallId">The provider's id for the call — unique within its round only.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="Arguments">The arguments as the model sent them.</param>
/// <param name="Result">The tool's answer, once the next request carried it back.</param>
internal sealed record TracedCall(int Round, string CallId, string Tool, IDictionary<string, object?>? Arguments, string? Result);
