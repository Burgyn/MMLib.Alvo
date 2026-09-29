using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The worked examples' replies are what models copy, so they must pass the grader the eval applies to models.</summary>
public sealed partial class InstructionRepliesTests
{
    private const int WorkedReplies = 8;

    private static readonly string _instructions = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Ai", "Instructions", "schema-assistant.md"));

    [Fact]
    public void Every_worked_example_reply_says_proposed_and_never_claims_done()
    {
        var replies = Reply().Matches(_instructions).Select(match => match.Groups["reply"].Value).ToList();

        replies.Count.ShouldBe(WorkedReplies);
        replies.ShouldAllBe(reply => ProposalWording.SaysProposed(reply) && !ProposalWording.ClaimsDone(reply));
    }

    [GeneratedRegex(@"Reply:[^*]*\*(?<reply>[^*]+)\*", RegexOptions.CultureInvariant)]
    private static partial Regex Reply();
}
