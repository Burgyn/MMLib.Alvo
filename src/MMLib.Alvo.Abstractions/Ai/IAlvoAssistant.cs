namespace MMLib.Alvo.Ai;

/// <summary>
/// The agent the dashboard talks to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port in Abstractions, so the dashboard never sees the agent package.</b> <c>MMLib.Alvo.Admin</c>
/// holds no reference to <c>MMLib.Alvo.Ai</c> any more than it does to <c>MMLib.Alvo</c>; what it resolves
/// is this interface, and a deployment that installed no agent resolves nothing at all — which is the same
/// "not configured" the connection itself answers.
/// </para>
/// <para>
/// <b>It can only propose.</b> Nothing on this interface applies a descriptor, and the implementation's tool
/// set has no member that writes: the guard is the composition rather than an instruction in a prompt, which
/// is the only kind of guard a model cannot be talked out of.
/// </para>
/// </remarks>
public interface IAlvoAssistant
{
    /// <summary>
    /// Answers one turn, streaming what happens while it does.
    /// </summary>
    /// <param name="request">The operator's message and the conversation it belongs to.</param>
    /// <param name="ct">A token to cancel the turn.</param>
    /// <returns>The updates, in the order they happened.</returns>
    /// <remarks>
    /// A failure arrives as <see cref="AssistantUpdate.Failed"/> rather than as an exception, for the reason
    /// that record gives: the caller is drawing a conversation, and "the AI connection is not configured" is
    /// a sentence in the thread rather than something to catch.
    /// </remarks>
    IAsyncEnumerable<AssistantUpdate> AskAsync(AssistantRequest request, CancellationToken ct = default);
}
