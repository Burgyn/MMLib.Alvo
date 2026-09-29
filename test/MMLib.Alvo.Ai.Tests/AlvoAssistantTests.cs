using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;

using NSubstitute;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// What a turn produces, and the three things it must never do.
/// </summary>
/// <remarks>
/// No test here reaches a model: <see cref="ScriptedChatClient"/> is the endpoint, and every behaviour
/// asserted is one this class decides before a token would be sent.
/// </remarks>
public sealed class AlvoAssistantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// With nothing configured the turn says so, and never touches the Management API.
    /// </summary>
    /// <remarks>
    /// The second half is the one worth measuring: an assistant that read the project before discovering it
    /// had no endpoint would be doing authorised work on behalf of a feature that is switched off.
    /// </remarks>
    [Fact]
    public async Task With_no_connection_the_turn_says_so_and_reads_nothing()
    {
        var management = Substitute.For<IAlvoManagement>();
        var updates = await RunAsync(management, Unconfigured(), new ScriptedChatClient());

        updates.ShouldHaveSingleItem().ShouldBeOfType<AssistantUpdate.Failed>()
            .Reason.ShouldContain("No AI connection is configured");
        await management.DidNotReceiveWithAnyArgs().GetDescriptorAsync(default!, Ct);
    }

    /// <summary>A turn that calls a tool says which one, so a screen can show what is happening.</summary>
    [Fact]
    public async Task A_tool_call_is_reported_by_name()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetDescriptorAsync("p", Arg.Any<CancellationToken>())
            .Returns(new ManagementDescriptor("p", 2, """{"name":"p"}"""));

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
            Scripted.Calls("get_descriptor", []),
            Scripted.Says("It declares one entity.")));

        updates.OfType<AssistantUpdate.ToolInvoked>().Select(update => update.Tool)
            .ShouldBe(["get_descriptor"]);
        updates.OfType<AssistantUpdate.Proposal>().ShouldBeEmpty();
    }

    /// <summary>
    /// A valid proposal becomes the turn's proposal, carrying the revision the apply must echo.
    /// </summary>
    /// <remarks>
    /// And it arrives <b>after</b> the dry run, never before: the proposal is built from what the dry run
    /// recorded, and the descriptor it carries is the applied one with the patch applied.
    /// </remarks>
    [Fact]
    public async Task A_valid_proposal_becomes_the_turns_proposal_after_the_dry_run()
    {
        var management = Describing(revision: 4);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision: 4, EmptyPlan));

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says("Adds a nullable note column.")));

        var proposal = updates.OfType<AssistantUpdate.Proposal>().ShouldHaveSingleItem();
        proposal.ExpectedRevision.ShouldBe(4);
        JsonNode.Parse(proposal.DescriptorJson)!["entities"]!["bikes"]!["fields"]!["notes"].ShouldNotBeNull();
        proposal.Summary.ShouldContain("nullable note column");
        updates.IndexOf(proposal).ShouldBe(updates.Count - 1);
    }

    /// <summary>A turn that ends without an answer of its own is summarised by what it proposed (D10).</summary>
    [Fact]
    public async Task A_proposal_with_no_answer_text_is_summarised_by_the_proposals_own_summary()
    {
        var management = Describing(revision: 4);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision: 4, EmptyPlan));

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says(string.Empty)));

        updates.OfType<AssistantUpdate.Proposal>().ShouldHaveSingleItem().Summary.ShouldBe("Adds notes.");
    }

    /// <summary>A question answered with a dry run is an answer, not a proposal.</summary>
    [Fact]
    public async Task A_turn_that_only_checked_a_change_proposes_nothing()
    {
        var management = Describing(revision: 4);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision: 4, EmptyPlan));
        var checking = Proposing(revision: 4);
        checking.Remove("summary");

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
            Scripted.Calls("check_change", checking),
            Scripted.Says("Yes, that would work.")));

        updates.OfType<AssistantUpdate.Proposal>().ShouldBeEmpty();
    }

    /// <summary>A refused retry after a valid proposal leaves the valid one standing.</summary>
    [Fact]
    public async Task A_later_refused_attempt_does_not_replace_the_valid_proposal()
    {
        var management = Describing(revision: 4);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new ManagementApplyResult(Applied: false, Revision: 4, EmptyPlan),
                _ => throw new DescriptorValidationException(new DescriptorValidationResult(
                    [new DescriptorValidationError("/entities", "No.", null, DescriptorValidationSeverity.Error)])));

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says("Done.")));

        updates.OfType<AssistantUpdate.Proposal>().ShouldHaveSingleItem().Refusals.ShouldBeEmpty();
    }

    /// <summary>A model that keeps calling tools ends as a turn, not a bill.</summary>
    /// <remarks>
    /// Thirty scripted calls against a cap of <see cref="AlvoAssistant.MaximumIterations"/>: at the framework's
    /// default of forty, every one of them would run. The count is pinned exactly — one tool call per iteration — so
    /// a loop that ended early for an unrelated reason cannot pass for the cap.
    /// </remarks>
    [Fact]
    public async Task A_model_that_never_stops_calling_tools_is_cut_off_at_the_iteration_cap()
    {
        var management = Describing(revision: 1);
        var looping = Enumerable.Range(0, 30).Select(_ => Scripted.Calls("get_descriptor", [])).ToArray();

        await RunAsync(management, Configured(), new ScriptedChatClient(looping));

        management.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IAlvoManagement.GetDescriptorAsync))
            .ShouldBe(AlvoAssistant.MaximumIterations);
    }

    /// <summary>The agent's own invoker is capped, and invokes one tool call at a time.</summary>
    /// <remarks>
    /// <see cref="Internal.ManagementTools"/> keeps its budget and proposal in plain fields; concurrent invocation
    /// would let parallel calls in one response all pass an exhausted budget, so the setting is pinned here.
    /// </remarks>
    [Fact]
    public void The_agents_invoker_is_capped_and_sequential()
    {
        var agent = AlvoAssistant.AgentFor(
            new ScriptedChatClient(), Internal.ManagementTools.For(Substitute.For<IAlvoManagement>(), "p"));

        var invoker = agent.ChatClient.GetService<FunctionInvokingChatClient>().ShouldNotBeNull();

        invoker.MaximumIterationsPerRequest.ShouldBe(AlvoAssistant.MaximumIterations);
        invoker.AllowConcurrentInvocation.ShouldBeFalse();
    }

    /// <summary>
    /// An endpoint that failed is a sentence in the thread, not an exception the dashboard must survive.
    /// </summary>
    /// <remarks>
    /// And the sentence carries none of the provider's own message: that routinely echoes the request — the
    /// base address, the header, sometimes the key — into a conversation an operator may screenshot.
    /// </remarks>
    [Fact]
    public async Task An_endpoint_that_fails_ends_the_turn_with_a_sentence_rather_than_a_throw()
    {
        var updates = await RunAsync(
            Substitute.For<IAlvoManagement>(), Configured(), new ThrowingChatClient("sk-live-leaked"));

        var failure = updates.ShouldHaveSingleItem().ShouldBeOfType<AssistantUpdate.Failed>();
        failure.Reason.ShouldContain("did not answer");
        failure.Reason.ShouldNotContain("sk-live-leaked");
    }

    /// <summary>
    /// Nothing the operator typed reaches the log.
    /// </summary>
    /// <remarks>
    /// A message to a schema assistant routinely carries a connection string somebody pasted, and a log line
    /// is the one place §7.1 asks a secret never to reach. Measured against a capturing logger rather than
    /// trusted, because the leak this guards against is one added statement away.
    /// </remarks>
    [Fact]
    public async Task The_operators_message_is_never_logged()
    {
        var logger = new CapturingLogger();

        await RunAsync(
            Substitute.For<IAlvoManagement>(),
            Configured(),
            new ThrowingChatClient("boom"),
            logger,
            message: "here is my key sk-live-do-not-log");

        logger.Lines.ShouldNotContain(line => line.Contains("sk-live-do-not-log", StringComparison.Ordinal));
        logger.Lines.ShouldNotBeEmpty();
    }

    private static async Task<List<AssistantUpdate>> RunAsync(
        IAlvoManagement management,
        IAiConnectionResolver connections,
        IChatClient client,
        ILogger<AlvoAssistant>? logger = null,
        string message = "add a note column")
    {
        var assistant = new AlvoAssistant(
            management, connections, _ => client, logger ?? new CapturingLogger());

        var updates = new List<AssistantUpdate>();
        await foreach (var update in assistant.AskAsync(new AssistantRequest("p", message, []), Ct))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static IAlvoManagement Describing(int revision)
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetDescriptorAsync("p", Arg.Any<CancellationToken>()).Returns(new ManagementDescriptor(
            "p", revision, """{"name":"p","entities":{"bikes":{"fields":{"brand":{"type":"string"}}}}}"""));

        return management;
    }

    private static Dictionary<string, object?> Proposing(int revision) => new()
    {
        ["baseRevision"] = revision,
        ["operations"] = JsonDocument.Parse(
            """[{"op":"add","path":"/entities/bikes/fields/notes","value":{"type":"text"}}]""").RootElement.Clone(),
        ["summary"] = "Adds notes.",
    };

    private static IAiConnectionResolver Configured() => Resolving(new AlvoAiConnection(
        AiConnectionKind.OpenAiCompatible, new Uri("http://localhost:11434/v1"), "qwen3:8b", null));

    private static IAiConnectionResolver Unconfigured() => Resolving(null);

    /// <summary>A resolver answering one connection, or none.</summary>
    private static IAiConnectionResolver Resolving(AlvoAiConnection? connection)
    {
        var resolver = Substitute.For<IAiConnectionResolver>();

        // CA2012 reads the arranged call as a ValueTask nobody awaited. It is NSubstitute's arrangement
        // idiom: the call records an expectation and its result is never consumed as a task.
#pragma warning disable CA2012
        resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<AiConnectionResolution>(new AiConnectionResolution(
                connection,
                connection is null ? AiConnectionSource.None : AiConnectionSource.Store,
                connection is null ? AiKeyState.None : AiKeyState.Present)));
#pragma warning restore CA2012

        return resolver;
    }

    private static ManagementPlanSummary EmptyPlan { get; } = new(IsEmpty: true, HasDestructiveChanges: false, []);

    /// <summary>An endpoint that fails the way a wrong address or an expired key does.</summary>
    private sealed class ThrowingChatClient(string secret) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException($"401 Unauthorized for key {secret}");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException($"401 Unauthorized for key {secret}");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A logger that keeps what it was told, so a test can assert what was not.</summary>
    private sealed class CapturingLogger : ILogger<AlvoAssistant>
    {
        internal List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + " " + exception);
    }
}
