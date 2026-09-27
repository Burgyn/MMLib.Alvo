using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Testing.Secrets;
using MMLib.Alvo.Tests;
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

    /// <summary>
    /// A secret this instance never wrote is logged by name — never a throw, and never the value, because
    /// there is none to leak.
    /// </summary>
    /// <remarks>
    /// The live defect this closes (reported 24 Sep 2026): <c>ApiKeySecretRef</c> named a secret nobody had
    /// saved, <c>GET {m}/info</c> still reported the connection as configured, and the OpenAI client sent its
    /// own placeholder credential in the key's place — an operator staring at a 401 with no lead on why.
    /// </remarks>
    [Fact]
    public async Task A_referenced_secret_this_instance_does_not_have_is_warned_once_by_name()
    {
        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));

        var resolver = Resolver(
            new InMemorySecretStore(),
            new AlvoAiOptions
            {
                Kind = "openai-compatible",
                Endpoint = "https://api.openai.com/v1",
                Model = "gpt-5",
                ApiKeySecretRef = "openai.key",
            },
            loggers.CreateLogger<AiConnectionResolver>());

        var resolved = await resolver.ResolveAsync(Ct);

        resolved.Connection!.ApiKey.ShouldBeNull();
        capturing.Warnings.ShouldHaveSingleItem()
            .ShouldContain("openai.key", Case.Sensitive, "the operator has to know which name to save it under");
    }

    /// <summary>A secret the reference actually resolves never logs the "missing" warning.</summary>
    [Fact]
    public async Task A_referenced_secret_this_instance_has_is_not_warned()
    {
        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));
        var store = new InMemorySecretStore();
        await store.SetAsync(SecretName.Parse("openai.key"), "sk-live", Ct);

        var resolver = Resolver(
            store,
            new AlvoAiOptions
            {
                Kind = "openai-compatible",
                Endpoint = "https://api.openai.com/v1",
                Model = "gpt-5",
                ApiKeySecretRef = "openai.key",
            },
            loggers.CreateLogger<AiConnectionResolver>());

        await resolver.ResolveAsync(Ct);

        capturing.Warnings.ShouldBeEmpty();
    }

    /// <summary>
    /// OpenAI and Azure OpenAI both refuse an unauthenticated call outright, so a connection to either with
    /// no referenced key is not "maybe fine" the way a bare Ollama is — it is warned.
    /// </summary>
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://contoso.openai.azure.com")]
    public async Task An_endpoint_that_always_needs_a_key_is_warned_when_none_is_referenced(string endpoint)
    {
        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));

        var resolver = Resolver(
            new InMemorySecretStore(),
            new AlvoAiOptions { Kind = "openai-compatible", Endpoint = endpoint, Model = "gpt-5" },
            loggers.CreateLogger<AiConnectionResolver>());

        await resolver.ResolveAsync(Ct);

        capturing.Warnings.ShouldHaveSingleItem().ShouldContain("always needs a key");
    }

    /// <summary>A local endpoint with no key is routinely deliberate, so it is not warned.</summary>
    [Fact]
    public async Task An_endpoint_that_does_not_always_need_a_key_is_not_warned_when_none_is_referenced()
    {
        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));

        var resolver = Resolver(
            new InMemorySecretStore(),
            new AlvoAiOptions { Kind = "openai-compatible", Endpoint = "http://localhost:11434/v1", Model = "qwen3:8b" },
            loggers.CreateLogger<AiConnectionResolver>());

        await resolver.ResolveAsync(Ct);

        capturing.Warnings.ShouldBeEmpty();
    }

    /// <summary>
    /// A key the configured reference resolves is present — the one state Settings draws nothing extra for.
    /// </summary>
    [Fact]
    public async Task A_configured_key_the_reference_resolves_is_present()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(SecretName.Parse("openai.key"), "sk-live", Ct);

        var resolved = await Resolver(store, Configured("https://api.openai.com/v1", "openai.key")).ResolveAsync(Ct);

        resolved.KeyState.ShouldBe(AiKeyState.Present);
    }

    /// <summary>
    /// A reference to a secret this instance does not have is a missing key, whatever the endpoint — the live
    /// case (24 Sep 2026) where Settings said "connected" and every turn was a 401.
    /// </summary>
    /// <remarks>
    /// The local endpoint is the point: it is one that would otherwise read as "no key needed", and the
    /// operator who named a secret has said a key is needed.
    /// </remarks>
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    public async Task A_reference_to_a_secret_this_instance_does_not_have_is_a_missing_key(string endpoint)
    {
        var resolved = await Resolver(new InMemorySecretStore(), Configured(endpoint, "openai.key")).ResolveAsync(Ct);

        resolved.Connection.ShouldNotBeNull("a connection with no key is still a connection, just one that is refused");
        resolved.KeyState.ShouldBe(AiKeyState.Missing);
    }

    /// <summary>A reference that is not a secret name at all resolves nothing, so the key is missing too.</summary>
    [Fact]
    public async Task A_reference_that_is_not_a_secret_name_is_a_missing_key() =>
        (await Resolver(new InMemorySecretStore(), Configured("http://localhost:11434/v1", "OpenAI Key"))
            .ResolveAsync(Ct)).KeyState.ShouldBe(AiKeyState.Missing);

    /// <summary>
    /// A key pasted where its name goes is never written to a log — the most likely way to get this setting wrong,
    /// since it is literally named <c>ApiKey…</c>. The warning says what the setting must hold, and nothing of what it
    /// does.
    /// </summary>
    [Fact]
    public async Task A_key_pasted_in_place_of_its_name_never_reaches_a_log_line()
    {
        const string pasted = "sk-proj-Q7vX2mLp9RtY4wZ8nB3kD6fH1jS5aE0cU-yNoTaRealKeyButShapedLikeOne1234567890";
        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));

        var resolved = await Resolver(
            new InMemorySecretStore(),
            Configured("https://api.openai.com/v1", pasted),
            loggers.CreateLogger<AiConnectionResolver>()).ResolveAsync(Ct);

        resolved.KeyState.ShouldBe(AiKeyState.Missing);
        capturing.Entries.ShouldNotBeEmpty("the misconfiguration is still reported");
        capturing.Entries.ShouldAllBe(entry => !entry.Message.Contains("sk-proj", StringComparison.Ordinal)
            && !entry.Message.Contains("Q7vX2mLp9RtY4wZ8", StringComparison.Ordinal));
        capturing.Warnings.ShouldHaveSingleItem().ShouldContain(
            "Alvo:Ai:ApiKeySecretRef is not a secret name — it must name a secret, never hold the key itself");
    }

    /// <summary>A secret whose value is only whitespace is no key.</summary>
    [Fact]
    public async Task A_referenced_secret_of_only_whitespace_is_a_missing_key()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(SecretName.Parse("openai.key"), "   ", Ct);

        var resolved = await Resolver(store, Configured("https://api.openai.com/v1", "openai.key")).ResolveAsync(Ct);

        resolved.KeyState.ShouldBe(AiKeyState.Missing);
        resolved.Connection!.ApiKey.ShouldBeNull();
    }

    /// <summary>And a saved connection whose key is only whitespace has none.</summary>
    [Fact]
    public async Task A_stored_key_of_only_whitespace_is_no_key()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(
            AiConnectionResolver.StoredName,
            """{"kind":"openai-compatible","endpoint":"https://api.openai.com/v1","model":"gpt-5","apiKey":"  "}""",
            Ct);

        (await Resolver(store, new AlvoAiOptions()).ResolveAsync(Ct)).KeyState.ShouldBe(AiKeyState.Missing);
    }

    /// <summary>And so is a referenced key this build cannot decrypt.</summary>
    [Fact]
    public async Task A_referenced_key_this_build_cannot_decrypt_is_a_missing_key() =>
        (await Resolver(new RefusingStore(), Configured("https://api.openai.com/v1", "openai.key"))
            .ResolveAsync(Ct)).KeyState.ShouldBe(AiKeyState.Missing);

    /// <summary>
    /// A host that always needs a key, dialled with no reference at all, is a missing key rather than a
    /// keyless connection.
    /// </summary>
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://contoso.openai.azure.com")]
    [InlineData("https://contoso.cognitiveservices.azure.com")]
    [InlineData("https://contoso.services.ai.azure.com/models")]
    public async Task An_endpoint_that_always_needs_a_key_with_none_referenced_is_a_missing_key(string endpoint) =>
        (await Resolver(new InMemorySecretStore(), Configured(endpoint, reference: null))
            .ResolveAsync(Ct)).KeyState.ShouldBe(AiKeyState.Missing);

    /// <summary>A local endpoint with no reference is keyless on purpose, which is not a warning.</summary>
    [Fact]
    public async Task A_local_endpoint_with_no_reference_needs_no_key() =>
        (await Resolver(new InMemorySecretStore(), Configured("http://localhost:11434/v1", reference: null))
            .ResolveAsync(Ct)).KeyState.ShouldBe(AiKeyState.NotNeeded);

    /// <summary>A saved connection that carries its key has one.</summary>
    [Fact]
    public async Task A_stored_connection_with_a_key_has_one()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(AiConnectionResolver.StoredName, StoredJson(model: "qwen3:8b"), Ct);

        (await Resolver(store, new AlvoAiOptions()).ResolveAsync(Ct)).KeyState.ShouldBe(AiKeyState.Present);
    }

    /// <summary>
    /// A saved connection with no key is judged by the host it dials, the same rule a configured one is.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:11434/v1", AiKeyState.NotNeeded)]
    [InlineData("https://api.openai.com/v1", AiKeyState.Missing)]
    [InlineData("https://contoso.openai.azure.com", AiKeyState.Missing)]
    [InlineData("https://contoso.cognitiveservices.azure.com", AiKeyState.Missing)]
    [InlineData("https://contoso.services.ai.azure.com", AiKeyState.Missing)]
    [InlineData("https://azure.com.example", AiKeyState.NotNeeded)]
    public async Task A_stored_connection_with_no_key_is_judged_by_its_host(string endpoint, AiKeyState expected)
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(
            AiConnectionResolver.StoredName,
            $$"""{"kind":"openai-compatible","endpoint":"{{endpoint}}","model":"gpt-5"}""",
            Ct);

        (await Resolver(store, new AlvoAiOptions()).ResolveAsync(Ct)).KeyState.ShouldBe(expected);
    }

    /// <summary>No connection has no key to speak of.</summary>
    [Fact]
    public async Task No_connection_has_no_key_state() =>
        (await Resolver(new InMemorySecretStore(), new AlvoAiOptions()).ResolveAsync(Ct))
            .KeyState.ShouldBe(AiKeyState.None);

    /// <summary>An OpenAI-compatible connection this deployment pins, with the reference it names.</summary>
    private static AlvoAiOptions Configured(string endpoint, string? reference) => new()
    {
        Kind = "openai-compatible",
        Endpoint = endpoint,
        Model = "gpt-5",
        ApiKeySecretRef = reference,
    };

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
        Resolver(secrets, configured, NullLogger<AiConnectionResolver>.Instance);

    private static AiConnectionResolver Resolver(
        ISecretStore secrets, AlvoAiOptions configured, ILogger<AiConnectionResolver> logger) =>
        new AiConnectionResolver(new StaticOptions(configured), secrets, logger);

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
