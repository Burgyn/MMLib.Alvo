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
/// </remarks>
internal sealed class RecordingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private readonly Dictionary<string, RecordedCall> _calls = new(StringComparer.Ordinal);

    /// <summary>How many times the loop asked the model.</summary>
    internal int Requests { get; private set; }

    /// <summary>How many of those answers asked for at least one tool.</summary>
    internal int ToolRounds { get; private set; }

    /// <summary>The tokens the provider reported, in and out.</summary>
    internal long Tokens { get; private set; }

    /// <summary>Every tool call, in the order the model made them.</summary>
    internal IReadOnlyList<RecordedCall> Calls => [.. _calls.Values];

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
        foreach (var result in sent.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
        {
            if (_calls.TryGetValue(result.CallId, out var call) && call.Result is null)
            {
                _calls[result.CallId] = call with { Result = TextOf(result.Result) };
            }
        }

        return sent;
    }

    private void Recorded(IEnumerable<AIContent> contents)
    {
        var askedForTool = false;
        foreach (var content in contents)
        {
            if (content is FunctionCallContent call)
            {
                askedForTool = true;
                _calls[call.CallId] = new RecordedCall(call.CallId, call.Name, call.Arguments, Result: null);
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
/// <param name="CallId">The provider's id for the call.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="Arguments">The arguments as the model sent them.</param>
/// <param name="Result">The tool's answer, once the next request carried it back.</param>
internal sealed record RecordedCall(string CallId, string Tool, IDictionary<string, object?>? Arguments, string? Result);
