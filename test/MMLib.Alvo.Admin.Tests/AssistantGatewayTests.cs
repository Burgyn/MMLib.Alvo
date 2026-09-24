using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Secrets;
using NSubstitute;
using System.Text.Json;

// CA2012 reads every arranged ISecretStore call below as a ValueTask nobody awaited. They are
// NSubstitute's arrangement idiom: the call records an expectation and its result is never consumed
// as a task.
#pragma warning disable CA2012

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// What the dashboard does about the assistant when there is none, and what it writes when there is.
/// </summary>
public sealed class AssistantGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A deployment that installed no agent package has none, and says so rather than throwing.</summary>
    [Fact]
    public async Task With_no_agent_installed_the_turn_says_so_rather_than_throwing()
    {
        var gateway = Gateway(assistant: null, secrets: null);

        gateway.IsInstalled.ShouldBeFalse();

        var updates = new List<AssistantUpdate>();
        await foreach (var update in gateway.AskAsync(new AssistantRequest("p", "hello", []), Ct))
        {
            updates.Add(update);
        }

        updates.ShouldHaveSingleItem().ShouldBeOfType<AssistantUpdate.Failed>()
            .Reason.ShouldContain("MMLib.Alvo.Ai");
    }

    /// <summary>
    /// A deployment whose secrets come from configuration cannot save one, and the screen knows before it
    /// draws a control.
    /// </summary>
    /// <remarks>
    /// The common case rather than the broken one — every GitOps deployment is in it — and a save button
    /// whose only possible outcome is a refusal is worse than its absence.
    /// </remarks>
    [Fact]
    public async Task With_a_read_only_store_it_cannot_write_and_refuses_before_touching_it()
    {
        var store = Substitute.For<ISecretStore>();
        store.CanWrite.Returns(false);

        var gateway = Gateway(assistant: null, store);

        gateway.CanWriteConnection.ShouldBeFalse();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            async () => await gateway.SaveConnectionAsync(Form(), Ct));

        refusal.Message.ShouldContain("Alvo:Ai");
        await store.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, Ct);
    }

    /// <summary>
    /// A save writes one secret, under the one reserved name, and writes no other.
    /// </summary>
    /// <remarks>
    /// The endpoint, the model and the key change together; three names would leave a window in which the
    /// screen reports a model that is being dialled at the previous endpoint.
    /// </remarks>
    /// <summary>
    /// A save travels the Management API, never the store, so the core decides who may write a credential.
    /// </summary>
    /// <remarks>
    /// The whole record goes in one call: the endpoint, the model and the key change together, and three
    /// writes would leave a window in which a screen reports one and the agent dials another.
    /// </remarks>
    [Fact]
    public async Task A_save_travels_the_management_api_rather_than_the_store()
    {
        var management = Substitute.For<IAlvoManagement>();
        var store = Writable();

        await Gateway(assistant: null, store, management).SaveConnectionAsync(Form(), Ct);

        await management.Received(1).SetAiConnectionAsync(
            Arg.Is<StoredAiConnection>(record =>
                record.Kind == "openai-compatible"
                && record.Endpoint == "http://localhost:11434/v1"
                && record.Model == "qwen3:8b"
                && record.ApiKey == "sk-live"),
            Arg.Any<CancellationToken>());
        await store.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, Ct);
    }

    /// <summary>
    /// An operator the core refuses gets the core's refusal, and nothing is written.
    /// </summary>
    /// <remarks>
    /// This is the finding a review caught: before the write moved onto the management surface, any
    /// signed-in operator — including one at <c>read</c> — could repoint the assistant's endpoint at an
    /// address that then received the descriptor, the schema and their colleagues' prompts.
    /// </remarks>
    [Fact]
    public async Task An_operator_the_core_refuses_cannot_write_the_connection()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.SetAiConnectionAsync(Arg.Any<StoredAiConnection>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ManagementForbiddenException());

        await Should.ThrowAsync<ManagementForbiddenException>(
            async () => await Gateway(assistant: null, Writable(), management).SaveConnectionAsync(Form(), Ct));
    }

    /// <summary>An endpoint that needs no key is stored with none, not with an empty one.</summary>
    /// <remarks>
    /// An empty string sends an empty header, which some gateways refuse differently from a missing one —
    /// and a local Ollama is the case this screen exists to make easy.
    /// </remarks>
    [Fact]
    public async Task An_empty_key_is_stored_as_no_key()
    {
        var management = Substitute.For<IAlvoManagement>();

        await Gateway(assistant: null, Writable(), management).SaveConnectionAsync(Form(apiKey: "   "), Ct);

        await management.Received(1).SetAiConnectionAsync(
            Arg.Is<StoredAiConnection>(record => record.ApiKey == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A write the deployment's own configuration would shadow surfaces as a message, not as a crash.
    /// </summary>
    /// <remarks>
    /// It is the one failure this layering can produce, and the silent version is the worst kind: the
    /// operator saves a key, the screen says saved, and every request keeps using the old one.
    /// </remarks>
    [Fact]
    public async Task A_shadowed_write_surfaces_as_the_stores_own_refusal()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.SetAiConnectionAsync(Arg.Any<StoredAiConnection>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new SecretShadowedException(
                SecretName.Parse(StoredAiConnection.SecretName), "Alvo:Secrets:Values"));

        var refusal = await Should.ThrowAsync<SecretShadowedException>(
            async () => await Gateway(assistant: null, Writable(), management).SaveConnectionAsync(Form(), Ct));

        refusal.Message.ShouldContain("Alvo:Secrets:Values");
    }

    /// <summary>
    /// A saved connection announces itself, so a shell that already asked can ask again.
    /// </summary>
    /// <remarks>
    /// Reported from a phone: Settings said the connection was configured and no assistant launcher
    /// appeared anywhere, for the rest of the session. <c>AdminLayout</c> resolves "is one configured" once
    /// in <c>OnInitializedAsync</c>, and nothing told it the answer had moved — so the fix is this event and
    /// the subscription over it, and this is the half that can be measured without a browser.
    /// </remarks>
    [Fact]
    public async Task A_saved_connection_announces_itself()
    {
        var gateway = Gateway(assistant: null, Writable());
        var announced = 0;

        gateway.ConnectionChanged += () => announced++;

        await gateway.SaveConnectionAsync(Form(), Ct);

        announced.ShouldBe(1);
    }

    /// <summary>
    /// A refused save announces nothing.
    /// </summary>
    /// <remarks>
    /// The launcher is mounted off the announcement, so announcing a write that did not happen would light
    /// an assistant that cannot be dialled — the failure the read-only-store refusal exists to prevent,
    /// arriving one layer later.
    /// </remarks>
    [Fact]
    public async Task A_refused_save_announces_nothing()
    {
        var store = Substitute.For<ISecretStore>();
        store.CanWrite.Returns(false);

        var gateway = Gateway(assistant: null, store);
        var announced = 0;

        gateway.ConnectionChanged += () => announced++;

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await gateway.SaveConnectionAsync(Form(), Ct));

        announced.ShouldBe(0);
    }

    /// <summary>
    /// A turn runs with the operator published, for the whole turn.
    /// </summary>
    /// <remarks>
    /// This is the second finding a review caught. The agent reads the project through
    /// <c>IAlvoManagement</c>, which admits nobody it cannot see; without the publication every tool
    /// answered <c>forbidden</c> and the assistant was blind on any real deployment — the safe direction,
    /// and useless. It is measured at the moment the inner assistant is asked <em>and</em> at the moment a
    /// later update is pulled, because the tools run between one update and the next.
    /// </remarks>
    [Fact]
    public async Task A_turn_runs_with_the_operator_published()
    {
        var ambient = Substitute.For<IAlvoContextAccessor>();
        var callers = Substitute.For<IAlvoAdminCallerResolver>();
        callers.ResolveAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Operator);

        var assistant = new PrincipalWatchingAssistant(ambient);
        var gateway = new AssistantGateway(assistant, secrets: null, new ManagementGateway(
            Substitute.For<IAlvoManagement>(), people: null, callers, SignedIn(), ambient));

        await foreach (var _ in gateway.AskAsync(new AssistantRequest("p", "hello", []), Ct))
        {
        }

        assistant.SeenWhenAsked.ShouldBe(Operator);
        assistant.SeenMidTurn.ShouldBe(Operator);
        ambient.Principal.ShouldBeNull();
    }

    /// <summary>The caller the resolver answers with, and the one the tools must see.</summary>
    private static AlvoPrincipal Operator { get; } = new()
    {
        Context = AlvoContext.System(tenant: null),
        Scopes = new HashSet<ApiKeyScope>(),
        KeyId = "the-operator",
    };

    /// <summary>An assistant that records who was published when it was asked, and again mid-turn.</summary>
    private sealed class PrincipalWatchingAssistant(IAlvoContextAccessor ambient) : IAlvoAssistant
    {
        internal AlvoPrincipal? SeenWhenAsked { get; private set; }

        internal AlvoPrincipal? SeenMidTurn { get; private set; }

        public async IAsyncEnumerable<AssistantUpdate> AskAsync(
            AssistantRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            SeenWhenAsked = ambient.Principal;

            yield return new AssistantUpdate.Text("thinking");

            SeenMidTurn = ambient.Principal;

            yield return new AssistantUpdate.Text(" done");

            await Task.CompletedTask;
        }
    }

    private static ISecretStore Writable()
    {
        var store = Substitute.For<ISecretStore>();
        store.CanWrite.Returns(true);

        return store;
    }

    private static AiConnectionForm Form(string? apiKey = "sk-live") =>
        new("openai-compatible", "http://localhost:11434/v1", "qwen3:8b", apiKey);

    /// <summary>
    /// The gateway over substituted dependencies.
    /// </summary>
    /// <remarks>
    /// The management gateway is real but never asked anything: the only member these facts reach on it is
    /// <c>Invalidate</c>, which clears fields. Substituting the type itself is not available — it is sealed,
    /// which is the same reason the dashboard's other tests construct it too.
    /// </remarks>
    private static AssistantGateway Gateway(
        IAlvoAssistant? assistant, ISecretStore? secrets, IAlvoManagement? management = null) =>
        new(assistant, secrets, new ManagementGateway(
            management ?? Substitute.For<IAlvoManagement>(),
            people: null,
            Substitute.For<IAlvoAdminCallerResolver>(),
            SignedIn(),
            Substitute.For<IAlvoContextAccessor>()));

    /// <summary>An authentication state provider that answers, so the gateway can resolve a caller.</summary>
    /// <remarks>
    /// The principal it resolves to does not matter here: what these facts measure is <em>which</em> seam
    /// the write travels, and the core is what decides whether that caller may.
    /// </remarks>
    private static AuthenticationStateProvider SignedIn()
    {
        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal())));

        return authentication;
    }
}
