using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// Sits under the assistant's function-invoking loop, so every call through it is one model round-trip.
/// </summary>
/// <remarks>
/// <para>
/// It records what the grading needs and nothing else: the round-trips, the ones that asked for a tool, the tokens the
/// provider reported, each tool call's name and arguments, and each tool's answer as the next request carried it back.
/// </para>
/// <para>
/// <b>Tool rounds are what the iteration cap bounds, not requests.</b> The function-invoking loop invokes tools at most
/// <see cref="AlvoAssistant.MaximumIterations"/> times and then still asks the model for its answer, so a turn that
/// honoured the cap can make one request more than it; the round-trips are recorded, the tool rounds are graded.
/// </para>
/// <para>
/// <b>A call is identified by its round and its id, never its id alone.</b> Some OpenAI-compatible servers number call
/// ids per response (<c>call_0</c> every round), and keying by id would let a later round overwrite an earlier call. A
/// tool's answer is matched to the latest unanswered call with its id — the one the request answering it follows.
/// </para>
/// </remarks>
internal sealed class RecordingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private readonly List<RecordedCall> _calls = [];

    /// <summary>How many times the loop asked the model.</summary>
    internal int Requests { get; private set; }

    /// <summary>How many of those answers asked for at least one tool.</summary>
    internal int ToolRounds { get; private set; }

    /// <summary>The tokens the provider reported, in and out.</summary>
    internal long Tokens { get; private set; }

    /// <summary>How the harness's follow-up message starts (D47) — <see cref="Internal.FollowUp.Lead"/>, for the suite.</summary>
    internal const string FollowUpLead = Internal.FollowUp.Lead;

    /// <summary>How many follow-ups the turn sent (D47), each counted on the request whose last message it is.</summary>
    /// <remarks>
    /// A later request of the same session still carries the follow-up in its history, so "the last message" is what
    /// makes a request the one that sent it.
    /// </remarks>
    internal int FollowUps { get; private set; }

    /// <summary>Every tool call, in the order the model made them.</summary>
    internal IReadOnlyList<RecordedCall> Calls => [.. _calls];

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
        Requests++;
        FollowUps += sent is [.., { } last] && last.Role == ChatRole.User && last.Text.StartsWith(FollowUpLead, StringComparison.Ordinal) ? 1 : 0;
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
        var round = Requests;
        var askedForTool = false;
        foreach (var content in contents)
        {
            if (content is FunctionCallContent call)
            {
                askedForTool = true;
                _calls.Add(new RecordedCall(round, call.CallId, call.Name, call.Arguments, Result: null));
            }
            else if (content is UsageContent usage)
            {
                Tokens += usage.Details.TotalTokenCount ?? 0;
            }
        }

        ToolRounds += askedForTool ? 1 : 0;
    }

    private static string? TextOf(object? result) => result switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
        _ => result.ToString(),
    };
}

/// <summary>One tool call the model made, and what the tool answered.</summary>
/// <param name="Round">The request (1-based) whose answer asked for it.</param>
/// <param name="CallId">The provider's id for the call — unique within its round only.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="Arguments">The arguments as the model sent them.</param>
/// <param name="Result">The tool's answer, once the next request carried it back.</param>
internal sealed record RecordedCall(int Round, string CallId, string Tool, IDictionary<string, object?>? Arguments, string? Result);
