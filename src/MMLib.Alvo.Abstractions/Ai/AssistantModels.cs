namespace MMLib.Alvo.Ai;

/// <summary>Who said a turn.</summary>
public enum AssistantRole
{
    /// <summary>The person typing.</summary>
    Operator = 0,

    /// <summary>The agent answering.</summary>
    Assistant = 1,
}

/// <summary>One turn of a conversation, as the caller remembers it.</summary>
/// <remarks>
/// <b>The history is the caller's, not the agent's.</b> The assistant holds no session state between calls,
/// so a dashboard that drew a conversation is the one authority on what that conversation was — and a second
/// tab, a reload or a restarted process cannot resurrect a turn nobody can see.
/// </remarks>
/// <param name="Role">Who said it.</param>
/// <param name="Text">What was said.</param>
public sealed record AssistantTurn(AssistantRole Role, string Text);

/// <summary>What the operator asked, and the conversation it belongs to.</summary>
/// <param name="Project">The project the question is about; every tool call is scoped to it.</param>
/// <param name="Message">The operator's message.</param>
/// <param name="History">The turns before this one, oldest first.</param>
public sealed record AssistantRequest(string Project, string Message, IReadOnlyList<AssistantTurn> History);

/// <summary>
/// One thing that happened while the agent was answering.
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed hierarchy rather than a bag of nullable fields</b>, so a caller that handles three of the four
/// cases fails to compile rather than silently dropping the fourth — and the fourth is the proposal.
/// </para>
/// <para>
/// <b>Streamed, because a turn that calls four tools takes seconds.</b> A screen that showed nothing until
/// the end would be indistinguishable from one that had hung, and <see cref="ToolInvoked"/> is what lets it
/// say which question the agent is currently asking the framework.
/// </para>
/// </remarks>
public abstract record AssistantUpdate
{
    private AssistantUpdate()
    {
    }

    /// <summary>A fragment of the answer being written.</summary>
    /// <param name="Delta">The text to append; never the whole answer so far.</param>
    public sealed record Text(string Delta) : AssistantUpdate;

    /// <summary>The agent called one of its tools.</summary>
    /// <remarks>
    /// The name only. An argument list would carry the descriptor the operator is drafting into whatever the
    /// caller does with this — a log line, a transcript — and the tool set is a fixed five, so the name is
    /// the whole of what a reader can act on.
    /// </remarks>
    /// <param name="Tool">The tool's name, as the agent sees it.</param>
    public sealed record ToolInvoked(string Tool) : AssistantUpdate;

    /// <summary>
    /// The agent is proposing a descriptor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A proposal, never an apply.</b> The agent's tool set has no member that writes — the guard is
    /// composition rather than a sentence in a prompt — so this is the only way a change it drafted can
    /// reach the database, and it reaches it through the operator pressing the same button they press for
    /// their own edits.
    /// </para>
    /// <para>
    /// <b><see cref="ExpectedRevision"/> is what the apply must echo.</b> The agent read the descriptor at
    /// one revision and drafted against it; without the echo, two operators composing at once would
    /// overwrite each other with no sign that either had.
    /// </para>
    /// </remarks>
    /// <param name="DescriptorJson">The proposed descriptor, whole.</param>
    /// <param name="ExpectedRevision">The revision the agent read, which the apply must echo.</param>
    /// <param name="Summary">What the change does, in the agent's own words.</param>
    /// <param name="Refusals">
    /// What the dry run refused, <b>verbatim</b>. The framework's own wording rather than the agent's
    /// paraphrase of it: a reworded refusal is a second authority on what Alvo does, and the one an operator
    /// reads would be the one nobody tested.
    /// </param>
    public sealed record Proposal(
        string DescriptorJson,
        int ExpectedRevision,
        string Summary,
        IReadOnlyList<string> Refusals) : AssistantUpdate;

    /// <summary>The turn could not be answered.</summary>
    /// <remarks>
    /// An update rather than an exception, because the caller is drawing a conversation: an unconfigured
    /// connection and an endpoint that timed out are both things to say in the thread, not stack traces to
    /// catch around an enumerator.
    /// </remarks>
    /// <param name="Reason">What went wrong, in words an operator can act on and with no secret in them.</param>
    public sealed record Failed(string Reason) : AssistantUpdate;
}
