using Microsoft.Extensions.AI;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// A model that says what it was told to say.
/// </summary>
/// <remarks>
/// <para>
/// <b>No test in this suite reaches a model.</b> Everything <see cref="AlvoAssistant"/> owns is decided
/// before a token is sent — which tools exist, whether a draft was validated, what a proposal carries, what
/// an operator is told when the endpoint fails — and a test that dialled a real endpoint would measure the
/// provider's mood instead of any of it.
/// </para>
/// <para>
/// One scripted response per call, in order. The function-invoking pipeline the agent builds calls back
/// after it has run a tool, so a two-response script is how a turn that calls one tool and then answers is
/// written.
/// </para>
/// </remarks>
/// <param name="responses">What to return, one per call, in order.</param>
internal sealed class ScriptedChatClient(params IReadOnlyList<ChatResponse> responses) : IChatClient
{
    private int _calls;

    /// <summary>The messages the pipeline sent, in order — what a test asserts was never logged.</summary>
    internal List<ChatMessage> Sent { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Sent.AddRange(messages);

        return Task.FromResult(Next());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Sent.AddRange(messages);

        foreach (var update in Next().ToChatResponseUpdates())
        {
            yield return update;
        }

        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>The next scripted response, or a final empty answer once the script runs out.</summary>
    /// <remarks>
    /// Running out is not a failure: the pipeline calls once more than a script naturally describes when the
    /// last scripted response ends a turn, and a throw there would fail a test for the framework's normal
    /// behaviour.
    /// </remarks>
    private ChatResponse Next()
    {
        var index = _calls++;

        return index < responses.Count
            ? responses[index]
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
    }
}

/// <summary>Builds the responses a script is written from.</summary>
internal static class Scripted
{
    /// <summary>A turn of plain text.</summary>
    internal static ChatResponse Says(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>A turn that calls one tool.</summary>
    /// <param name="tool">The tool's name.</param>
    /// <param name="arguments">The arguments, by parameter name.</param>
    internal static ChatResponse Calls(string tool, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, (IList<AIContent>)
        [
            new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, arguments),
        ]));
}
