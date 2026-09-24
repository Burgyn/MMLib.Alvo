using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Admin;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// An assistant turn, from the dashboard's gateway down to the core, reads the project as the operator.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins</b> (reported 24 Sep 2026): with a working key, an admin asked which entities there
/// were, the model called <c>get_schema</c>, and the tool answered <c>forbidden</c>. The dashboard's gateway
/// published the operator once at the top of an async iterator, and the tools — which run after the model's
/// first streamed update — resumed on a context that had never seen it.
/// </para>
/// <para>
/// <b>Everything real but the model.</b> The management service, the core's own <c>AsyncLocal</c> accessor it
/// authorizes against, the dashboard's gateways, <see cref="AlvoAssistant"/> and the agent framework's
/// function-invoking loop over its real tool set. The dashboard's end-to-end suite replaces the assistant with
/// a scripted one that never calls a tool, and the assistant's own suite substitutes the management surface,
/// so neither could see a principal that was missing only on the way from one to the other. The model streams
/// text before it asks for the tool, because that ordering is what put the tool call on a later step.
/// </para>
/// </remarks>
public sealed class AssistantOperatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The schema the tool hands the model is the project's, not a refusal.</summary>
    [Fact]
    public async Task A_tool_called_after_streamed_text_reads_the_schema_as_the_operator()
    {
        await using var world = await AlvoHostWorld.StartAsync(ManagedDescriptor);
        var model = new TextThenSchemaClient();
        var ambient = world.Services.GetRequiredService<IAlvoContextAccessor>();

        var (updates, seenByConsumer) = await AskAsync(world, model, ambient);

        updates.OfType<AssistantUpdate.ToolInvoked>().Select(update => update.Tool).ShouldBe(["get_schema"]);
        var result = model.ToolResult.ShouldNotBeNull("the loop must hand the tool's answer back to the model");
        result.ShouldNotContain("forbidden");
        result.ShouldContain("warehouses");
        seenByConsumer.ShouldAllBe(
            principal => principal == null,
            "the consumer — what renders an update — never runs as the operator, only the stream's own steps "
            + "do; asserted after the whole turn instead, this would hold on the leaking code too, since the "
            + "operator is restored once the turn's own async iterator has finished either way");
    }

    /// <summary>
    /// One turn through the dashboard's gateways, collected alongside who the consumer — this loop's own
    /// body, standing in for whatever renders an update on a real circuit — saw published between two steps.
    /// </summary>
    private static async Task<(List<AssistantUpdate> Updates, List<AlvoPrincipal?> SeenByConsumer)> AskAsync(
        AlvoHostWorld world, IChatClient model, IAlvoContextAccessor ambient)
    {
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        var assistant = new AlvoAssistant(
            management, new Connected(), _ => model, NullLogger<AlvoAssistant>.Instance);
        var gateway = new AssistantGateway(assistant, secrets: null, new ManagementGateway(
            management, people: null, new AdminCaller(), new SignedIn(), ambient));

        var updates = new List<AssistantUpdate>();
        var seenByConsumer = new List<AlvoPrincipal?>();
        await foreach (var update in gateway.AskAsync(new AssistantRequest(Project, "which entities do I have?", []), Ct))
        {
            updates.Add(update);
            seenByConsumer.Add(ambient.Principal);
        }

        return (updates, seenByConsumer);
    }

    /// <summary>The descriptor whose <c>access</c> block admits an <c>admin</c> to the management surface.</summary>
    private const string ManagedDescriptor = "host-management.alvo.json";

    /// <summary>The project that descriptor declares.</summary>
    private const string Project = "host-management";

    /// <summary>
    /// A model that streams a sentence, then asks for <c>get_schema</c>, then answers from what it was given.
    /// </summary>
    /// <remarks>
    /// Each update is preceded by a real yield, so the loop resumes from a continuation between them, as it
    /// does over a network stream.
    /// </remarks>
    private sealed class TextThenSchemaClient : IChatClient
    {
        private int _calls;

        /// <summary>What the <c>get_schema</c> tool answered, as the model received it.</summary>
        internal string? ToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The assistant streams its turns.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Let me read the schema. ");

                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_schema")]);
                yield break;
            }

            ToolResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .LastOrDefault()?.Result?.ToString();

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "You have one entity.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A configured connection; the chat client the assistant is handed never dials it.</summary>
    private sealed class Connected : IAiConnectionResolver
    {
        public ValueTask<AiConnectionResolution> ResolveAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new AiConnectionResolution(
                new AlvoAiConnection(AiConnectionKind.OpenAiCompatible, new Uri("http://model.invalid/v1"), "m", null),
                AiConnectionSource.Configuration));
    }

    /// <summary>The operator: an <c>admin</c>, which the descriptor's <c>access</c> block admits as a viewer.</summary>
    private sealed class AdminCaller : IAlvoAdminCallerResolver
    {
        public ValueTask<AlvoPrincipal?> ResolveAsync(ClaimsPrincipal signedIn, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AlvoPrincipal?>(new AlvoPrincipal
            {
                Context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Admin } },
                Scopes = new HashSet<ApiKeyScope>(),
                KeyId = "the-operator",
            });
    }

    /// <summary>Somebody signed in; who is the caller resolver's business.</summary>
    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity("test"))));
    }
}
