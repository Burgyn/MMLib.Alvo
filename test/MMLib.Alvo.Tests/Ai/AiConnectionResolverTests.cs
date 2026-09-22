using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Testing.Secrets;

using System.Security.Cryptography;

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

        var resolved = await resolver.ResolveAsync(Ct);
        resolved.Connection!.Model.ShouldBe("from-configuration");
        resolved.Source.ShouldBe(AiConnectionSource.Configuration);
    }

    /// <summary>No AI is the default state of every deployment, so it is an answer rather than a refusal.</summary>
    [Fact]
    public async Task With_nothing_configured_it_resolves_to_null()
    {
        var resolver = Resolver(new InMemorySecretStore(), new AlvoAiOptions());

        var resolved = await resolver.ResolveAsync(Ct);
        resolved.Connection.ShouldBeNull();
        resolved.Source.ShouldBe(AiConnectionSource.None);
    }

    [Fact]
    public async Task The_stored_connection_is_read_through_the_secret_store()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(AiConnectionResolver.StoredName, StoredJson(model: "qwen3:8b"), Ct);

        var resolver = Resolver(store, new AlvoAiOptions());
        var resolved = await resolver.ResolveAsync(Ct);

        resolved.Connection!.Model.ShouldBe("qwen3:8b");
        resolved.Connection.Kind.ShouldBe(AiConnectionKind.OpenAiCompatible);
        resolved.Connection.ApiKey.ShouldBe("sk-stored");
        resolved.Source.ShouldBe(AiConnectionSource.Store);
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

        (await resolver.ResolveAsync(Ct)).Connection!.ApiKey.ShouldBe("sk-live");
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

        (await Resolver(store, new AlvoAiOptions()).ResolveAsync(Ct)).Connection.ShouldBeNull();
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

        (await Resolver(store, new AlvoAiOptions()).ResolveAsync(Ct)).Connection.ShouldBeNull();
    }

    /// <summary>Half a configured connection cannot be dialled, so it is not one.</summary>
    [Fact]
    public async Task A_configured_connection_with_no_endpoint_resolves_to_null() =>
        (await Resolver(new InMemorySecretStore(), new AlvoAiOptions { Kind = "openai-compatible", Model = "x" })
            .ResolveAsync(Ct)).Connection.ShouldBeNull();

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
            .ResolveAsync(Ct)).Connection.ShouldBeNull();

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
            .ResolveAsync(Ct)).Connection!.Kind.ShouldBe(AiConnectionKind.AzureOpenAi);

    /// <summary>
    /// A stored row this build cannot decrypt is no connection, not a failed page.
    /// </summary>
    /// <remarks>
    /// The store authenticates as it decrypts, so a tampered value — or one written under a key that has
    /// since been rotated away — arrives as a <see cref="CryptographicException"/>. The screen asking this
    /// is drawing a status; letting the throw through would replace the settings page that exists to fix
    /// the row with a circuit error.
    /// </remarks>
    [Fact]
    public async Task A_stored_connection_this_build_cannot_decrypt_resolves_to_nothing()
    {
        var resolver = Resolver(new RefusingStore(), new AlvoAiOptions());

        var resolved = await resolver.ResolveAsync(Ct);

        resolved.Connection.ShouldBeNull();
        resolved.Source.ShouldBe(AiConnectionSource.None);
    }

    /// <summary>And so is a referenced API key it cannot decrypt — the connection loses its key, not its life.</summary>
    [Fact]
    public async Task A_key_this_build_cannot_decrypt_leaves_the_connection_without_one()
    {
        var resolver = Resolver(new RefusingStore(), new AlvoAiOptions
        {
            Kind = "openai-compatible",
            Endpoint = "https://api.openai.com/v1",
            Model = "gpt-5",
            ApiKeySecretRef = "openai.key",
        });

        (await resolver.ResolveAsync(Ct)).Connection!.ApiKey.ShouldBeNull();
    }

    /// <summary>A store whose every read is a value it cannot authenticate.</summary>
    private sealed class RefusingStore : ISecretStore
    {
        public bool CanWrite => false;

        public ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default) =>
            throw new CryptographicException("The authentication tag did not match.");

        public ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default) =>
            new((IReadOnlyList<SecretName>)[]);
    }

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
