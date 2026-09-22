using Microsoft.AspNetCore.Components.Authorization;

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
    [Fact]
    public async Task A_save_writes_one_secret_under_the_reserved_name()
    {
        var store = Writable();
        var written = new List<(SecretName Name, string Value)>();
        store.WhenForAnyArgs(candidate => candidate.SetAsync(default!, default!, Ct))
            .Do(call => written.Add((call.Arg<SecretName>(), call.Arg<string>())));

        await Gateway(assistant: null, store).SaveConnectionAsync(Form(), Ct);

        var one = written.ShouldHaveSingleItem();
        one.Name.Value.ShouldBe(StoredAiConnection.SecretName);

        var record = JsonSerializer.Deserialize(
            one.Value, StoredAiConnectionJsonContext.Default.StoredAiConnection)!;
        record.Kind.ShouldBe("openai-compatible");
        record.Endpoint.ShouldBe("http://localhost:11434/v1");
        record.Model.ShouldBe("qwen3:8b");
        record.ApiKey.ShouldBe("sk-live");
    }

    /// <summary>An endpoint that needs no key is stored with none, not with an empty one.</summary>
    /// <remarks>
    /// An empty string sends an empty header, which some gateways refuse differently from a missing one —
    /// and a local Ollama is the case this screen exists to make easy.
    /// </remarks>
    [Fact]
    public async Task An_empty_key_is_stored_as_no_key()
    {
        var store = Writable();
        string? stored = null;
        store.WhenForAnyArgs(candidate => candidate.SetAsync(default!, default!, Ct))
            .Do(call => stored = call.Arg<string>());

        await Gateway(assistant: null, store).SaveConnectionAsync(Form(apiKey: "   "), Ct);

        JsonSerializer.Deserialize(stored!, StoredAiConnectionJsonContext.Default.StoredAiConnection)!
            .ApiKey.ShouldBeNull();
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
        var store = Writable();
        store.SetAsync(Arg.Any<SecretName>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new SecretShadowedException(
                SecretName.Parse(StoredAiConnection.SecretName), "Alvo:Secrets:Values"));

        var refusal = await Should.ThrowAsync<SecretShadowedException>(
            async () => await Gateway(assistant: null, store).SaveConnectionAsync(Form(), Ct));

        refusal.Message.ShouldContain("Alvo:Secrets:Values");
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
    private static AssistantGateway Gateway(IAlvoAssistant? assistant, ISecretStore? secrets) =>
        new(assistant, secrets, new ManagementGateway(
            Substitute.For<IAlvoManagement>(),
            people: null,
            Substitute.For<IAlvoAdminCallerResolver>(),
            Substitute.For<AuthenticationStateProvider>(),
            Substitute.For<IAlvoContextAccessor>()));
}
