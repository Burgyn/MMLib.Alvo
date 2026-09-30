using Microsoft.Extensions.AI;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

using NSubstitute;

using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Tests.AlvoAssistantTests;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The one follow-up a turn gets when it stopped on a refusal it could still fix (spec §9, D47): when it is sent, when it
/// is not, what it costs, and that its words reach only the model.
/// </summary>
public sealed class AlvoAssistantFollowUpTests
{
    private static readonly ManagementPlanSummary _emptyPlan = new(IsEmpty: false, HasDestructiveChanges: false, []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The RCA 2 turn (spec §9.1): refused with attempts left, the model answers; the harness follows up once, the
    /// retry is valid and filed, and the operator reads only the answer that came after it (D47).
    /// </summary>
    [Fact]
    public async Task A_turn_that_stops_on_a_refusal_with_attempts_left_is_followed_up_once_and_its_retry_is_filed()
    {
        var management = Refusing(then: Valid());
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says("It was refused."),
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says("I proposed a notes field."));

        var updates = await RunAsync(management, model);

        updates.OfType<AssistantUpdate.Proposal>().ShouldHaveSingleItem().Refusals.ShouldBeEmpty();
        string.Concat(updates.OfType<AssistantUpdate.Text>().Select(text => text.Delta)).ShouldBe("I proposed a notes field.");
        FollowUps(model).ShouldBe(1);
    }

    /// <summary>The follow-up continues the same conversation: it is sent after the refused call's own result.</summary>
    [Fact]
    public async Task The_follow_up_is_sent_after_the_refused_calls_result_in_the_same_session()
    {
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("It was refused."), Scripted.Says("Still refused."));

        await RunAsync(Refusing(then: null), model);

        var followUp = model.Requests.Single(request => IsFollowUp(request[^1]));
        followUp.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ShouldHaveSingleItem();
        followUp[^1].Text.ShouldContain("/entities/bikes/fields/notes");
    }

    /// <summary>A follow-up run that ends refused again is not followed up: one per turn, the most (D47).</summary>
    [Fact]
    public async Task A_turn_is_followed_up_at_most_once()
    {
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused."),
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused again."));

        var updates = await RunAsync(Refusing(then: null), model);

        FollowUps(model).ShouldBe(1);
        string.Concat(updates.OfType<AssistantUpdate.Text>().Select(text => text.Delta)).ShouldBe("Refused again.");
    }

    /// <summary>What the budget, the operator or the build must decide is never followed up (D47 (c)–(e)).</summary>
    [Theory]
    [InlineData("attempts-spent")]
    [InlineData("destructive")]
    [InlineData("access")]
    [InlineData("unsupported")]
    public async Task A_refusal_the_model_cannot_fix_is_not_followed_up(string why)
    {
        var model = new ScriptedChatClient(Stopping(why));

        var updates = await RunAsync(Stopped(why), model);

        FollowUps(model).ShouldBe(0);
        updates.OfType<AssistantUpdate.Text>().ShouldNotBeEmpty();
    }

    /// <summary>A valid proposal, or a refused <c>check_change</c> (an answer to "would this work?"), is never followed up.</summary>
    [Theory]
    [InlineData("check_change")]
    [InlineData("valid")]
    public async Task A_turn_that_proposed_validly_or_only_checked_is_not_followed_up(string shape)
    {
        var model = new ScriptedChatClient(
            Scripted.Calls(shape == "valid" ? "propose_change" : "check_change", shape == "valid" ? Proposing(4) : Checking(4)),
            Scripted.Says("Answered."));

        await RunAsync(shape == "valid" ? Answering(Valid()) : Refusing(then: null), model);

        FollowUps(model).ShouldBe(0);
    }

    /// <summary>A followed-up turn still makes at most <see cref="AlvoAssistant.MaximumIterations"/> tool rounds.</summary>
    /// <remarks>
    /// Counted as invocations of a tool nothing else calls (<c>get_revisions</c>), not as <c>ToolInvoked</c> updates: the
    /// invoker's last response at the cap may name a call it never runs, and the draft pipeline reads the descriptor
    /// itself, so neither of those counts is one call per round.
    /// </remarks>
    [Fact]
    public async Task A_followed_up_turn_never_passes_the_iteration_cap()
    {
        var management = Refusing(then: null);
        var first = Enumerable.Range(0, AlvoAssistant.MaximumIterations - 2).Select(_ => Scripted.Calls("get_revisions", []));
        var looping = Enumerable.Range(0, 30).Select(_ => Scripted.Calls("get_revisions", []));
        var model = new ScriptedChatClient(
            [.. first, Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused."), .. looping]);

        await RunAsync(management, model);

        FollowUps(model).ShouldBe(1);
        await management.Received(AlvoAssistant.MaximumIterations - 1).ListRevisionsAsync("p", Arg.Any<CancellationToken>());
        await management.ReceivedWithAnyArgs(1).ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>The trace marks the follow-up by round; its words reach no update, no trace byte and no log line.</summary>
    [Fact]
    public async Task The_follow_up_is_marked_in_the_trace_and_its_words_reach_nothing_the_operator_sees()
    {
        var logger = new CapturingLogger();
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused."), Scripted.Says("Stopped."));

        var updates = await RunAsync(Refusing(then: null), model, logger, trace: true);

        var trace = updates.OfType<AssistantUpdate.TurnTraced>().Single().Json;
        JsonNode.Parse(trace)!["followUpAfterRound"]!.GetValue<int>().ShouldBe(2);
        trace.ShouldNotContain(FollowUp.Lead.Trim());
        logger.Lines.ShouldAllBe(line => !line.Contains(FollowUp.Lead.Trim(), StringComparison.Ordinal));
        updates.OfType<AssistantUpdate.Text>().ShouldAllBe(text => !text.Delta.Contains(FollowUp.Lead.Trim(), StringComparison.Ordinal));
    }

    /// <summary>The endpoint failing during the follow-up shows the held answer first, then the failure.</summary>
    [Fact]
    public async Task An_endpoint_failure_during_the_follow_up_shows_the_held_answer_then_the_failure()
    {
        var model = new FailingAfter(2, new ScriptedChatClient(Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused.")));

        var updates = await RunAsync(Refusing(then: null), model);

        updates.OfType<AssistantUpdate.Text>().ShouldHaveSingleItem().Delta.ShouldBe("Refused.");
        updates[^1].ShouldBeOfType<AssistantUpdate.Failed>();
    }

    private static int FollowUps(ScriptedChatClient model) => model.Requests.Count(request => IsFollowUp(request[^1]));

    private static bool IsFollowUp(ChatMessage message) =>
        message.Role == ChatRole.User && message.Text.StartsWith(FollowUp.Lead, StringComparison.Ordinal);

    private static Task<List<AssistantUpdate>> RunAsync(
        IAlvoManagement management, IChatClient model, CapturingLogger? logger = null, bool trace = false) =>
        AlvoAssistantTests.RunAsync(management, Configured(), model, logger, trace: trace);

    /// <summary>A valid dry run's answer.</summary>
    private static ManagementApplyResult Valid() => new(Applied: false, Revision: 4, _emptyPlan);

    /// <summary>A project whose first dry run is refused at <c>/entities/bikes/fields/notes</c>, and whose next answers <paramref name="then"/> — or is refused again.</summary>
    private static IAlvoManagement Refusing(ManagementApplyResult? then)
    {
        var management = Arranged([]);
        var refusal = Refusal("No.");
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw refusal, _ => then ?? throw refusal);

        return management;
    }

    /// <summary>A project whose every dry run answers <paramref name="result"/>.</summary>
    private static IAlvoManagement Answering(ManagementApplyResult result)
    {
        var management = Arranged([]);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>()).Returns(result);

        return management;
    }

    /// <summary>The script of each row of the no-follow-up theory: one refused call (three for the budget), then an answer.</summary>
    private static ChatResponse[] Stopping(string why) => why == "attempts-spent"
        ?
        [
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("The budget is spent."),
        ]
        : [Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("I cannot fix that.")];

    /// <summary>The project of each row of the no-follow-up theory.</summary>
    private static IAlvoManagement Stopped(string why) => why switch
    {
        "attempts-spent" => Refusing(then: null),
        "destructive" => Throwing(Arranged([]), new DestructiveChangeNotAllowedException("p", new MigrationPlan
        {
            Steps = [new MigrationStep(
                new SchemaChange { Kind = SchemaChangeKind.DropField, Entity = "bikes", Field = "brand" },
                IsDestructive: true,
                "Dropping bikes.brand discards every value in it.")],
        })),
        "access" => Throwing(Arranged([]), new ManagementEscalationException()),
        "unsupported" => Throwing(
            Arranged([new ManagementRefusedFeature("field.validation", "Not in this build.", "Remove it.")]), Refusal("Not in this build.")),
        _ => throw new ArgumentOutOfRangeException(nameof(why), why, "No such row."),
    };

    /// <summary>
    /// The descriptor at revision 4, the capabilities (refusing <paramref name="refused"/>), and an empty history —
    /// each arranged, because NSubstitute answers a sealed record with <see langword="null"/>.
    /// </summary>
    private static IAlvoManagement Arranged(IReadOnlyList<ManagementRefusedFeature> refused)
    {
        var management = Describing(revision: 4);
        management.GetCapabilitiesAsync("p", Arg.Any<CancellationToken>()).Returns(new ManagementCapabilities([], [], refused));
        management.ListRevisionsAsync("p", Arg.Any<CancellationToken>()).Returns([]);

        return management;
    }

    private static IAlvoManagement Throwing(IAlvoManagement management, Exception refusal)
    {
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns<ManagementApplyResult>(_ => throw refusal);

        return management;
    }

    private static DescriptorValidationException Refusal(string message) => new(new DescriptorValidationResult(
        [new DescriptorValidationError("/entities/bikes/fields/notes", message, "Fix it.", DescriptorValidationSeverity.Error)]));

    /// <summary>An endpoint that answers <c>requests</c> requests and then fails the way a dropped connection does.</summary>
    private sealed class FailingAfter(int requests, IChatClient inner) : DelegatingChatClient(inner)
    {
        private int _requests;

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            ++_requests > requests
                ? throw new HttpRequestException("The endpoint went away.")
                : base.GetResponseAsync(messages, options, cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            ++_requests > requests
                ? throw new HttpRequestException("The endpoint went away.")
                : base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
