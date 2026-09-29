using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The worked examples' replies are what models copy, so they must pass the grader the eval applies to models.</summary>
public sealed partial class InstructionRepliesTests
{
    private const int WorkedReplies = 9;
    private const string ExampleMarker = "<!-- example: ";
    private const string ValidOutcome = "{\"valid\": true";

    private static readonly string _instructions = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Ai", "Instructions", "schema-assistant.md"));

    [Fact]
    public void Every_worked_example_but_the_refused_first_attempt_has_a_reply() =>
        Replies().Count.ShouldBe(WorkedReplies);

    [Fact]
    public void No_worked_example_reply_claims_done() =>
        Replies().ShouldAllBe(reply => !ProposalWording.ClaimsDone(reply.Text));

    [Fact]
    public void Every_valid_worked_examples_reply_says_proposed() =>
        Replies().Where(reply => reply.Valid).ShouldAllBe(reply => ProposalWording.SaysProposed(reply.Text));

    [Fact]
    public void A_worked_example_replies_in_slovak() =>
        Replies().ShouldContain(reply => reply.Text.StartsWith("Navrhol som", StringComparison.Ordinal));

    /// <summary>Each example's reply, and whether the example's outcome is valid.</summary>
    private static List<(string Text, bool Valid)> Replies() =>
    [
        .. _instructions.Split(ExampleMarker).Skip(1)
            .Select(section => (Reply: Reply().Match(section), Valid: section.Contains(ValidOutcome, StringComparison.Ordinal)))
            .Where(example => example.Reply.Success)
            .Select(example => (example.Reply.Groups["reply"].Value, example.Valid)),
    ];

    [GeneratedRegex(@"Reply:[^*]*\*(?<reply>[^*]+)\*", RegexOptions.CultureInvariant)]
    private static partial Regex Reply();
}
