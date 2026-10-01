namespace MMLib.Alvo.Ai.Internal;

/// <summary>The one message the harness sends a model that stopped on a refusal it could still fix (D47).</summary>
/// <remarks>
/// Framework text, never the operator's: it reaches the model only, as a user-role turn (a system turn after the first
/// is refused by several OpenAI-compatible servers), led by <see cref="Lead"/> so the model does not quote it as the
/// operator's words. It carries validator pointers and nothing else.
/// </remarks>
internal static class FollowUp
{
    internal const string Lead = "Alvo (not the operator): ";

    internal static string Message(IReadOnlyList<string> pointers) =>
        $"{Lead}your last propose_change was refused at {string.Join(", ", pointers)}, and attempts are left. "
        + "Load any skill a violation names, apply each violation's fix at its pointer, and call propose_change again. "
        + "Answer the operator only after a valid proposal, or when the refusal says the construct is unsupported or "
        + "every fix adds something the operator did not ask for, or removes or changes what they asked for.";
}
