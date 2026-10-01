using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// Plays a second operator who applies a change while the turn is running: once, after the model has read the
/// descriptor and before its next request, so the change the model then proposes is written against a stale revision.
/// </summary>
/// <remarks>
/// <para>
/// The forced refusal of D15. It is forced by the world rather than by the prompt: every refusal a prompt can reliably
/// provoke is one the instructions exist to prevent, whereas a concurrent apply is refused whatever the model knows.
/// It sits under the recorder, so the request it delays is still counted as one round-trip.
/// </para>
/// <para>
/// <b>It waits for a <c>get_descriptor</c> call.</b> A model that proposes without reading the descriptor first is
/// never interfered with, so its turn is graded <c>forced=False</c> and fails the case — the force did not land, and
/// the case says so rather than passing a turn that recovered from nothing.
/// </para>
/// </remarks>
internal sealed class InterferingChatClient(IChatClient inner, Func<CancellationToken, Task> interfere) : DelegatingChatClient(inner)
{
    private const string DescriptorRead = "get_descriptor";
    private bool _interfered;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        await InterfereOnceAsync(sent, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        await InterfereOnceAsync(sent, cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(sent, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Whether the history holds a descriptor read — the point after which the second operator applies.</summary>
    private static bool IsDue(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Any(call => call.Name == DescriptorRead);

    private async Task InterfereOnceAsync(List<ChatMessage> sent, CancellationToken ct)
    {
        if (_interfered || !IsDue(sent))
        {
            return;
        }

        _interfered = true;
        await interfere(ct).ConfigureAwait(false);
    }
}
