using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Secrets;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// An Alvo host with end-user sign-in, over a real engine: the Data API world plus <c>AddAlvoAccounts</c> and
/// <c>MapAlvoAccounts</c>, an in-process OIDC issuer the Google preset is pointed at, a fake clock every lifetime is judged
/// on, and every log line captured at Trace.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ID tokens are Google-shaped and Google-issued</b> (<c>iss: https://accounts.google.com</c>), signed by
/// <see cref="Oidc"/>, whose handler the preset's overridden authority reaches — so the exchange runs the production
/// preset end to end, issuer spellings included, and no network is touched.
/// </para>
/// <para>
/// <b>An operator key</b> (<see cref="Operator"/>) is the deployment's bootstrap administrator, so a fact can re-apply the
/// descriptor through the Management API (<see cref="ReapplyAsync"/>) without the descriptor declaring an
/// <c>access</c> block of its own.
/// </para>
/// </remarks>
internal sealed class AccountsWorld : IAsyncDisposable
{
    /// <summary>The issuer every access token is minted as.</summary>
    internal const string Issuer = "https://alvo.test/auth/v1";

    /// <summary>The OAuth client id the Google preset accepts.</summary>
    internal const string ClientId = "client-1";

    /// <summary>The path of the token endpoint.</summary>
    internal const string TokenPath = "/auth/v1/token";

    /// <summary>The issuer Google signs as, which <see cref="IdToken"/> mints with.</summary>
    internal const string GoogleIssuer = "https://accounts.google.com";

    /// <summary>The instant every world's clock starts at.</summary>
    internal static DateTimeOffset Start { get; } = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The operator's API key: the bootstrap administrator, for re-applying the descriptor.</summary>
    internal static TestApiKey Operator { get; } = new("accounts-operator", ["authenticated"], ["*:read", "*:write"]);

    private readonly string _keyFile;

    private readonly string _project;

    private AccountsWorld(AlvoApiWorld api, TestOidcIssuer oidc, FakeTimeProvider clock, CapturedLogs logs, string keyFile, string project)
    {
        Api = api;
        Oidc = oidc;
        Clock = clock;
        Logs = logs;
        _keyFile = keyFile;
        _project = project;
    }

    /// <summary>Gets the host, its routes and its database.</summary>
    internal AlvoApiWorld Api { get; }

    /// <summary>Gets the OIDC issuer the Google preset is pointed at.</summary>
    internal TestOidcIssuer Oidc { get; }

    /// <summary>Gets the clock every lifetime is judged on.</summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>Gets every line the host logged, at every level.</summary>
    internal CapturedLogs Logs { get; }

    /// <summary>Gets the account store the host signs people in through.</summary>
    internal IAccountStore Store => Api.Services.GetRequiredService<IAccountStore>();

    /// <summary>Starts a world.</summary>
    /// <param name="engine">The engine the world runs on.</param>
    /// <param name="configure">Changes to the accounts options after the world's own.</param>
    /// <param name="descriptor">The descriptor fixture's file name under <c>descriptors/</c>.</param>
    /// <param name="api">Changes to the Data API's options.</param>
    internal static async Task<AccountsWorld> StartAsync(
        AlvoApiEngine engine,
        Action<AlvoAccountsOptions>? configure = null,
        string descriptor = "time-tracker.alvo.json",
        Action<AlvoApiOptions>? api = null)
    {
        var clock = new FakeTimeProvider(Start);
        var oidc = new TestOidcIssuer(clock);
        var logs = new CapturedLogs();
        var keyFile = Path.Combine(Path.GetTempPath(), $"alvo-accounts-world-{Guid.NewGuid():N}.key");
        await File.WriteAllTextAsync(keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), TestContext.Current.CancellationToken);
        var setup = new AlvoApiWorldSetup(
            ConfigureApi: api,
            MapManagementApi: true,
            ConfigureServicesAfterAlvo: services => AddAccounts(services, clock, oidc, logs, keyFile, configure),
            ConfigureApp: app => app.MapAlvoAccounts());
        var world = await AlvoApiWorld.FromDescriptorAsync(descriptor, [Operator], setup, engine);
        return new AccountsWorld(world, oidc, clock, logs, keyFile, ProjectOf(descriptor));
    }

    /// <summary>A Google ID token for <paramref name="subject"/>, fresh on the world's clock.</summary>
    /// <param name="subject">The <c>sub</c>.</param>
    /// <param name="email">The <c>email</c>, or none.</param>
    /// <param name="emailVerified">The <c>email_verified</c>.</param>
    /// <param name="hd">The Workspace domain, or none.</param>
    internal string IdToken(string subject, string? email = "ada@gmail.com", bool emailVerified = true, string? hd = null)
        => Oidc.Mint(subject, email, emailVerified, hd, audience: ClientId, issuer: GoogleIssuer);

    /// <summary>Posts <paramref name="form"/> to the token endpoint.</summary>
    /// <param name="form">The parameters, in order; a name may repeat.</param>
    internal Task<HttpResponseMessage> TokenAsync(params (string Name, string Value)[] form)
        => TokenAsync(new FormUrlEncodedContent(form.Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value))));

    /// <summary>Posts <paramref name="content"/> to the token endpoint, verbatim.</summary>
    /// <param name="content">The body.</param>
    internal Task<HttpResponseMessage> TokenAsync(HttpContent content)
        => Api.SendRawAsync(HttpMethod.Post, TokenPath, content: content);

    /// <summary>Exchanges <paramref name="idToken"/> for an Alvo session (RFC 8693).</summary>
    /// <param name="idToken">The subject token.</param>
    internal Task<HttpResponseMessage> ExchangeAsync(string idToken) => TokenAsync(
        ("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
        ("subject_token", idToken),
        ("subject_token_type", "urn:ietf:params:oauth:token-type:id_token"));

    /// <summary>Signs <paramref name="subject"/> in, asserting the exchange succeeded.</summary>
    /// <param name="subject">The <c>sub</c>.</param>
    /// <param name="email">The <c>email</c>, or none.</param>
    /// <param name="hd">The Workspace domain, or none.</param>
    internal async Task<(string Access, string Refresh)> SignInAsync(string subject, string? email = "ada@gmail.com", string? hd = null)
    {
        using var response = await ExchangeAsync(IdToken(subject, email, hd: hd));
        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return (body["access_token"]!.GetValue<string>(), body["refresh_token"]!.GetValue<string>());
    }

    /// <summary>Presents <paramref name="refresh"/> to the refresh grant (RFC 6749 §6).</summary>
    /// <param name="refresh">The refresh token.</param>
    internal Task<HttpResponseMessage> RefreshAsync(string refresh)
        => TokenAsync(("grant_type", "refresh_token"), ("refresh_token", refresh));

    /// <summary>Applies the current descriptor with one edit, through the Management API, as the operator.</summary>
    /// <param name="edit">The edit to make to the whole descriptor.</param>
    internal async Task ReapplyAsync(Action<JsonObject> edit)
    {
        var path = $"/management/projects/{_project}/descriptor";
        using var read = await Api.SendAsync(HttpMethod.Get, path, Operator);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.ReadTextAsync());
        var current = await read.ReadJsonObjectAsync();
        var descriptor = JsonNode.Parse(current["descriptorJson"]!.GetValue<string>())!.AsObject();
        edit(descriptor);
        var revision = current["revision"]!.GetValue<int>();
        using var applied = await Api.SendAsync(
            HttpMethod.Put,
            path,
            Operator,
            body: new JsonObject { ["descriptorJson"] = descriptor.ToJsonString(), ["author"] = "accounts-world", ["reason"] = "a sign-in fact" },
            headers: [new KeyValuePair<string, string>("If-Match", $"\"{revision}\"")]);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.ReadTextAsync());
    }

    /// <summary>How many rows a framework table holds, by its suffix (e.g. <c>_auth_accounts</c>).</summary>
    /// <param name="suffix">The table's <c>AlvoFrameworkTables</c> suffix.</param>
    internal Task<long> CountAsync(string suffix)
        => Api.CountRowsAsync(Api.Services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix + suffix);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        Oidc.Dispose();
        File.Delete(_keyFile);
    }

    private static void AddAccounts(
        IServiceCollection services,
        FakeTimeProvider clock,
        TestOidcIssuer oidc,
        CapturedLogs logs,
        string keyFile,
        Action<AlvoAccountsOptions>? configure)
    {
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.Configure<AlvoSecretOptions>(options => options.EncryptionKeyFile = keyFile);
        services.AddSingleton<IAlvoBootstrapAdmin>(new OperatorIsAdmin());
        services.AddAlvoAccounts(options =>
        {
            options.Issuer = Issuer;
            var google = new AlvoOidcProviderOptions { Authority = TestOidcIssuer.Authority };
            google.ClientIds.Add(ClientId);
            options.Providers["google"] = google;
            configure?.Invoke(options);
        });
        oidc.UseIn(services);
    }

    private static string ProjectOf(string descriptor)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "descriptors", descriptor)));
        return document.RootElement.GetProperty("name").GetString()!;
    }

    /// <summary>The operator key's user is the bootstrap administrator; nobody else is.</summary>
    private sealed class OperatorIsAdmin : IAlvoBootstrapAdmin
    {
        public bool IsBootstrapAdmin(UserId user) => user.Value == Operator.User;
    }
}

/// <summary>Every line a host logged: the formatted message, and the exception's text when there is one.</summary>
internal sealed class CapturedLogs : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Gets every line logged so far.</summary>
    internal IReadOnlyList<string> Lines => [.. _lines];

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => this;

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _lines.Enqueue(exception is null ? formatter(state, exception) : $"{formatter(state, exception)} {exception}");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
