using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Testing.Secrets;

namespace MMLib.Alvo.Tests.Ai;

/// <summary>
/// Which layer answers, what a half-written one does, and the one thing a resolved connection must never
/// print.
/// </summary>
public class AiConnectionResolverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The same precedence the secret store itself uses, so there is one rule rather than two.</summary>
    [Fact]
    public async Task Configuration_wins_over_the_stored_connection()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(AiConnectionResolver.StoredName, StoredJson(model: "from-store"), Ct);

        var resolver = Resolver(store, new AlvoAiOptions
        {
            Kind = "openai-compatible",
            Endpoint = "http://localhost:11434/v1",
            Model = "from-configuration",
        });

        (await resolver.ResolveAsync(Ct))!.Model.ShouldBe("from-configuration");
        (await resolver.DescribeSourceAsync(Ct)).ShouldBe(AiConnectionSource.Configuration);
    }

    /// <summary>No AI is the default state of every deployment, so it is an answer rather than a refusal.</summary>
    [Fact]
    public async Task With_nothing_configured_it_resolves_to_null()
    {
        var resolver = Resolver(new InMemorySecretStore(), new AlvoAiOptions());

        (await resolver.ResolveAsync(Ct)).ShouldBeNull();
        (await resolver.DescribeSourceAsync(Ct)).ShouldBe(AiConnectionSource.None);
    }

    [Fact]
    public async Task The_stored_connection_is_read_through_the_secret_store()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(AiConnectionResolver.StoredName, StoredJson(model: "qwen3:8b"), Ct);

        var resolver = Resolver(store, new AlvoAiOptions());
        var connection = await resolver.ResolveAsync(Ct);

        connection!.Model.ShouldBe("qwen3:8b");
        connection.Kind.ShouldBe(AiConnectionKind.OpenAiCompatible);
        connection.ApiKey.ShouldBe("sk-stored");
        (await resolver.DescribeSourceAsync(Ct)).ShouldBe(AiConnectionSource.Store);
    }

    /// <summary>
    /// A configured connection names its key rather than carrying it.
    /// </summary>
    /// <remarks>
    /// This is what keeps the GitOps path honest: the deployment pins the endpoint and the model in a file
    /// anybody can read, and the credential still comes from wherever that deployment keeps credentials.
    /// </remarks>
    [Fact]
    public async Task A_configured_key_is_read_from_the_store_by_reference()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(SecretName.Parse("openai.key"), "sk-live", Ct);

        var resolver = Resolver(store, new AlvoAiOptions
        {
            Kind = "openai-compatible",
            Endpoint = "https://api.openai.com/v1",
            Model = "gpt-5",
            ApiKeySecretRef = "openai.key",
        });

        (await resolver.ResolveAsync(Ct))!.ApiKey.ShouldBe("sk-live");
    }

    /// <summary>
    /// A resolved connection never prints its key.
    /// </summary>
    /// <remarks>
    /// The record's generated <c>ToString</c> prints every member, so one interpolated log line would put the
    /// credential in a log file. Asserted here rather than trusted, because the override is one keystroke
    /// from being deleted by someone tidying up.
    /// </remarks>
    [Fact]
    public void The_connection_never_prints_its_key()
    {
        var printed = new AlvoAiConnection(
            AiConnectionKind.OpenAiCompatible, new Uri("https://api.openai.com/v1"), "gpt-5", "sk-live")
            .ToString();

        printed.ShouldNotContain("sk-live");
        printed.ShouldContain("gpt-5");
    }

    /// <summary>A bad row must not take down the settings page that exists to fix it.</summary>
    [Fact]
    public async Task An_unparsable_stored_connection_resolves_to_null_rather_than_throwing()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(AiConnectionResolver.StoredName, "not json", Ct);

        (await Resolver(store, new AlvoAiOptions()).ResolveAsync(Ct)).ShouldBeNull();
    }

    /// <summary>And so must a row that parses but does not describe a connection.</summary>
    [Fact]
    public async Task A_stored_connection_missing_its_model_resolves_to_null()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(
            AiConnectionResolver.StoredName,
            """{"kind":"openai-compatible","endpoint":"http://localhost:11434/v1"}""",
            Ct);

        (await Resolver(store, new AlvoAiOptions()).ResolveAsync(Ct)).ShouldBeNull();
    }

    /// <summary>Half a configured connection cannot be dialled, so it is not one.</summary>
    [Fact]
    public async Task A_configured_connection_with_no_endpoint_resolves_to_null() =>
        (await Resolver(new InMemorySecretStore(), new AlvoAiOptions { Kind = "openai-compatible", Model = "x" })
            .ResolveAsync(Ct)).ShouldBeNull();

    /// <summary>
    /// A kind this build has no adapter for is no connection.
    /// </summary>
    /// <remarks>
    /// Including the numeric spelling, which <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// would accept and bind to a member no adapter can dial.
    /// </remarks>
    [Theory]
    [InlineData("anthropic")]
    [InlineData("1")]
    [InlineData("OpenAiCompatible")]
    public async Task A_kind_this_build_cannot_dial_is_no_connection(string kind) =>
        (await Resolver(
            new InMemorySecretStore(),
            new AlvoAiOptions { Kind = kind, Endpoint = "http://localhost:11434/v1", Model = "x" })
            .ResolveAsync(Ct)).ShouldBeNull();

    [Fact]
    public async Task Azure_is_the_second_kind_this_build_dials() =>
        (await Resolver(
            new InMemorySecretStore(),
            new AlvoAiOptions
            {
                Kind = "azure-openai",
                Endpoint = "https://contoso.openai.azure.com",
                Model = "gpt-5",
            })
            .ResolveAsync(Ct))!.Kind.ShouldBe(AiConnectionKind.AzureOpenAi);

    private static AiConnectionResolver Resolver(ISecretStore secrets, AlvoAiOptions configured) =>
        new AiConnectionResolver(
            new StaticOptions(configured), secrets, NullLogger<AiConnectionResolver>.Instance);

    private static string StoredJson(string model) =>
        $$"""
        {"kind":"openai-compatible","endpoint":"http://localhost:11434/v1","model":"{{model}}","apiKey":"sk-stored"}
        """;

    /// <summary>The bound options, without a host to bind them.</summary>
    private sealed class StaticOptions(AlvoAiOptions options) : IOptionsMonitor<AlvoAiOptions>
    {
        public AlvoAiOptions CurrentValue => options;

        public AlvoAiOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<AlvoAiOptions, string?> listener) => null;
    }
}
