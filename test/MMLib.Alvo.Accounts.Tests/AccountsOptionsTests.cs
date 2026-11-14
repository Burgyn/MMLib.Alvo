using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Migrations;
using NSubstitute;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The startup refusals of an accounts-enabled host (spec §5.1, §11.2; S2 Review Focus 4): each fails the start rather
/// than the first sign-in, and each names the setting to change.
/// </summary>
/// <remarks>
/// Every fact builds a real generic host with <c>AddAlvo</c> over SQLite and <c>AddAlvoAccounts</c>, runs the host's own
/// <see cref="IStartupValidator"/> — exactly what <c>StartAsync</c> runs, so the refusal proven is
/// <c>ValidateOnStart</c>'s wiring, not a validator called by hand — and starts what it admits. The validator is run
/// first rather than through <c>StartAsync</c> because the generic host activates its hosted services before validating,
/// and a composition with no relational store cannot activate Alvo's boot service at all.
/// </remarks>
public sealed class AccountsOptionsTests : IDisposable
{
    private const string GoogleClientId = "1234-web.apps.googleusercontent.com";

    private const string HttpMetadataEventName = "HttpMetadataAllowed";

    private static readonly string _descriptorPath = Path.Combine(AppContext.BaseDirectory, "descriptors", "reads-desk.alvo.json");

    private readonly string _database = Path.Combine(Path.GetTempPath(), $"alvo-accounts-{Guid.NewGuid():N}.db");

    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"alvo-accounts-{Guid.NewGuid():N}.key");

    /// <summary>Mounts the secrets key file the valid cases point at.</summary>
    public AccountsOptionsTests() => File.WriteAllText(_keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    /// <inheritdoc/>
    public void Dispose()
    {
        File.Delete(_keyFile);
        File.Delete(_database);
    }

    [Fact]
    public async Task A_valid_configuration_starts()
        => (await RefusalAsync(Composition.Valid(_keyFile))).ShouldBeNull();

    [Fact]
    public async Task A_host_without_a_secrets_key_file_is_refused_naming_the_setting()
    {
        var refusal = await RefusalAsync(Composition.Valid(keyFile: null));

        refusal.ShouldNotBeNull().Message.ShouldContain("Alvo:Secrets:EncryptionKeyFile is not set");
    }

    [Fact]
    public async Task A_key_file_that_is_not_mounted_is_refused_naming_the_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"alvo-absent-{Guid.NewGuid():N}.key");

        var refusal = await RefusalAsync(Composition.Valid(missing));

        refusal.ShouldNotBeNull().Message.ShouldContain($"Alvo:Secrets:EncryptionKeyFile is '{missing}', and no file is there");
    }

    [Fact]
    public async Task An_issuer_is_required_outside_development()
    {
        var withoutIssuer = Composition.Valid(_keyFile) with { Issuer = null };

        var production = await RefusalAsync(withoutIssuer with { Environment = Environments.Production });
        var development = await RefusalAsync(withoutIssuer with { Environment = Environments.Development });

        production.ShouldNotBeNull().Message.ShouldContain("Alvo:Accounts:Issuer is not set");
        development.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://api.example.test/auth/v1")]
    [InlineData("auth/v1")]
    public async Task An_issuer_that_is_not_an_absolute_https_uri_is_refused(string issuer)
    {
        var refusal = await RefusalAsync(Composition.Valid(_keyFile, options => options.Issuer = issuer));

        refusal.ShouldNotBeNull().Message.ShouldContain($"Alvo:Accounts:Issuer is '{issuer}', which is not an absolute https URI");
    }

    [Fact]
    public async Task An_http_authority_is_refused_unless_allowed_and_then_warned_on_every_start()
    {
        const string authority = "http://issuer.test";
        var refused = await RefusalAsync(Composition.Valid(_keyFile, options => Google(options).Authority = authority));
        var log = new HttpMetadataWarnings();
        var allowed = Composition.Valid(_keyFile, options => AllowHttp(Google(options), authority)) with { Log = log };

        var first = await RefusalAsync(allowed);
        var second = await RefusalAsync(allowed);

        refused.ShouldNotBeNull().Message.ShouldContain(
            "Alvo:Accounts:Providers:Google:Authority is 'http://issuer.test', an http:// authority");
        refused.Message.ShouldContain("Alvo:Accounts:Providers:Google:AllowHttpMetadata=true");
        (first, second).ShouldBe((null, null));
        log.Lines.Count.ShouldBe(2, "one warning per provider on every start");
        log.Lines.ShouldAllBe(line => line.Contains("Google") && line.Contains(authority));
    }

    [Fact]
    public async Task An_https_authority_starts_without_a_warning()
    {
        var log = new HttpMetadataWarnings();
        var composition = Composition.Valid(_keyFile, options => Google(options).Authority = "https://issuer.test") with { Log = log };

        (await RefusalAsync(composition)).ShouldBeNull();
        log.Lines.ShouldBeEmpty();
    }

    /// <summary>An authority that is no absolute URI is refused naming only the schemes this provider may use.</summary>
    [Theory]
    [InlineData(false, "which is not an absolute https URI.")]
    [InlineData(true, "which is not an absolute http or https URI.")]
    public async Task An_authority_that_is_not_absolute_names_the_schemes_this_provider_may_use(bool allowHttp, string expected)
    {
        var refusal = await RefusalAsync(Composition.Valid(_keyFile, options =>
        {
            Google(options).Authority = "issuer.test";
            Google(options).AllowHttpMetadata = allowHttp;
        }));

        refusal.ShouldNotBeNull().Message.ShouldContain($"Alvo:Accounts:Providers:Google:Authority is 'issuer.test', {expected}");
    }

    /// <summary>
    /// Review Focus 4: the header both channels would read is refused once accounts are on — and only then, so an
    /// existing host that never added accounts keeps starting exactly as S1 left it, key file or not.
    /// </summary>
    [Fact]
    public async Task An_api_key_header_named_authorization_is_refused_beside_accounts()
    {
        var withAccounts = Composition.Valid(_keyFile) with { ApiKeyHeader = "authorization" };
        var withoutAccounts = withAccounts with { Accounts = false, KeyFile = null };

        var refusal = await RefusalAsync(withAccounts);

        refusal.ShouldNotBeNull().Message.ShouldContain("Alvo:Auth:HeaderName is 'authorization'");
        refusal.Message.ShouldContain("Bearer");
        (await RefusalAsync(withoutAccounts)).ShouldBeNull();
    }

    [Theory]
    [InlineData(nameof(AlvoAccountsOptions.AccessTokenMinutes), 0)]
    [InlineData(nameof(AlvoAccountsOptions.AccessTokenMinutes), 61)]
    [InlineData(nameof(AlvoAccountsOptions.RefreshIdleDays), 0)]
    [InlineData(nameof(AlvoAccountsOptions.SessionLifetimeDays), 29)]
    [InlineData(nameof(AlvoAccountsOptions.RefreshReuseGraceSeconds), -1)]
    [InlineData(nameof(AlvoAccountsOptions.RefreshReuseGraceSeconds), 61)]
    public async Task Out_of_range_lifetimes_are_refused(string setting, int value)
    {
        var refusal = await RefusalAsync(Composition.Valid(_keyFile, options => Set(options, setting, value)));

        refusal.ShouldNotBeNull().Message.ShouldContain($"Alvo:Accounts:{setting} is {value}; it must be");
    }

    [Theory]
    [InlineData(nameof(AlvoAccountsOptions.AccessTokenMinutes), 1)]
    [InlineData(nameof(AlvoAccountsOptions.AccessTokenMinutes), 60)]
    [InlineData(nameof(AlvoAccountsOptions.SessionLifetimeDays), 30)]
    [InlineData(nameof(AlvoAccountsOptions.RefreshReuseGraceSeconds), 0)]
    [InlineData(nameof(AlvoAccountsOptions.RefreshReuseGraceSeconds), 60)]
    public async Task Lifetimes_on_the_boundary_start(string setting, int value)
        => (await RefusalAsync(Composition.Valid(_keyFile, options => Set(options, setting, value)))).ShouldBeNull();

    [Fact]
    public async Task A_prefix_without_a_leading_slash_is_refused()
    {
        var refusal = await RefusalAsync(Composition.Valid(_keyFile, options => options.Prefix = "auth/v1"));

        refusal.ShouldNotBeNull().Message.ShouldContain("Alvo:Accounts:Prefix is 'auth/v1'; it must start with '/'");
    }

    [Fact]
    public async Task A_host_with_no_provider_is_refused()
    {
        var refusal = await RefusalAsync(Composition.Valid(_keyFile, options => options.Providers.Clear()));

        refusal.ShouldNotBeNull().Message.ShouldContain("Alvo:Accounts:Providers names no identity provider");
    }

    [Fact]
    public async Task A_provider_this_build_does_not_offer_is_refused()
    {
        var refusal = await RefusalAsync(Composition.Valid(
            _keyFile, options => options.Providers["Microsoft"] = WithClient(new AlvoOidcProviderOptions())));

        refusal.ShouldNotBeNull().Message.ShouldContain(
            "Alvo:Accounts:Providers:Microsoft: the provider 'Microsoft' is not available in this build");
    }

    [Fact]
    public async Task A_provider_without_a_client_id_is_refused()
    {
        var refusal = await RefusalAsync(Composition.Valid(_keyFile, options => Google(options).ClientIds.Clear()));

        refusal.ShouldNotBeNull().Message.ShouldContain("Alvo:Accounts:Providers:Google:ClientIds must list at least one");
    }

    [Fact]
    public async Task A_host_without_a_relational_store_is_refused()
    {
        var refusal = await RefusalAsync(Composition.Valid(_keyFile) with { Relational = false });

        refusal.ShouldNotBeNull().Message.ShouldContain(
            "Accounts need a relational data provider: UseSqlite or UsePostgreSql");
    }

    /// <summary>Every refusal at once: one restart per fix is a crash loop an operator cannot read.</summary>
    [Fact]
    public async Task Every_refusal_is_reported_together()
    {
        var refusal = await RefusalAsync(Composition.Valid(keyFile: null) with { Issuer = null });

        refusal.ShouldNotBeNull().Failures.Count().ShouldBe(2);
        refusal.Message.ShouldContain("Alvo:Accounts:Issuer");
        refusal.Message.ShouldContain("Alvo:Secrets:EncryptionKeyFile");
    }

    private async Task<OptionsValidationException?> RefusalAsync(Composition composition)
    {
        using var host = Build(composition);
        try
        {
            host.Services.GetRequiredService<IStartupValidator>().Validate();
        }
        catch (OptionsValidationException refusal)
        {
            return refusal;
        }

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        return null;
    }

    private IHost Build(Composition composition)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = composition.Environment,
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(composition.Settings());
        builder.Logging.AddProvider(composition.Log ?? new HttpMetadataWarnings());
        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));
        AddData(builder.Services, composition.Relational);
        if (composition.Accounts)
        {
            builder.Services.AddAlvoAccounts(composition.Configure);
        }

        return builder.Build();
    }

    private void AddData(IServiceCollection services, bool relational)
    {
        if (relational)
        {
            services.AddAlvo(alvo => alvo.UseSqlite($"Data Source={_database}").FromDescriptor(_descriptorPath));
            return;
        }

        services.AddSingleton(Substitute.For<ISchemaMigrator>());
        services.AddAlvo();
    }

    private static AlvoOidcProviderOptions Google(AlvoAccountsOptions options) => options.Providers["Google"];

    private static void AllowHttp(AlvoOidcProviderOptions provider, string authority)
    {
        provider.Authority = authority;
        provider.AllowHttpMetadata = true;
    }

    private static AlvoOidcProviderOptions WithClient(AlvoOidcProviderOptions provider)
    {
        provider.ClientIds.Add(GoogleClientId);
        return provider;
    }

    private static void Set(AlvoAccountsOptions options, string setting, int value)
    {
        switch (setting)
        {
            case nameof(AlvoAccountsOptions.AccessTokenMinutes): options.AccessTokenMinutes = value; break;
            case nameof(AlvoAccountsOptions.RefreshIdleDays): options.RefreshIdleDays = value; break;
            case nameof(AlvoAccountsOptions.SessionLifetimeDays): options.SessionLifetimeDays = value; break;
            case nameof(AlvoAccountsOptions.RefreshReuseGraceSeconds): options.RefreshReuseGraceSeconds = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(setting), setting, "Not a lifetime setting.");
        }
    }

    /// <summary>One host to start: what it registers, and what it is configured with.</summary>
    private sealed record Composition(string? KeyFile, Action<AlvoAccountsOptions>? Tweak)
    {
        public string Environment { get; init; } = Environments.Production;

        public bool Accounts { get; init; } = true;

        public bool Relational { get; init; } = true;

        public string? ApiKeyHeader { get; init; }

        public string? Issuer { get; init; } = "https://api.example.test/auth/v1";

        public HttpMetadataWarnings? Log { get; init; }

        public static Composition Valid(string? keyFile, Action<AlvoAccountsOptions>? tweak = null) => new(keyFile, tweak);

        public void Configure(AlvoAccountsOptions options)
        {
            options.Issuer = Issuer;
            options.Providers["Google"] = WithClient(new AlvoOidcProviderOptions());
            Tweak?.Invoke(options);
        }

        public IEnumerable<KeyValuePair<string, string?>> Settings() =>
        [
            .. Setting("Alvo:Secrets:EncryptionKeyFile", KeyFile),
            .. Setting("Alvo:Auth:HeaderName", ApiKeyHeader),
        ];

        private static IEnumerable<KeyValuePair<string, string?>> Setting(string key, string? value)
            => value is null ? [] : [new(key, value)];
    }

    /// <summary>Every warning the accounts package logs about an http:// authority, and nothing else.</summary>
    private sealed class HttpMetadataWarnings : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && eventId.Name == HttpMetadataEventName)
            {
                _lines.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
