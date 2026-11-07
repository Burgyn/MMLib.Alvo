using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Secrets;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// <c>AddRequiredSecret</c> over a real boot: a missing or empty secret refuses the start by name, a present one lets it
/// finish, and no secret value — held or pasted in the wrong place — reaches a refusal, an apply's answer or a log line.
/// </summary>
public sealed class RequiredSecretTests
{
    private const string Present = "present-secret";
    private const string PresentValue = "s3cr3t-value";
    private const string PastedSecret = "sk_live_51Hq+Zs/9QwErTy==";
    private const string Desk = "order-desk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_required_secret_refuses_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartAsync(alvo => alvo.AddRequiredSecret("missing-secret")));

        refusal.Message.ShouldContain("'missing-secret'");
        refusal.FixSuggestion.ShouldContain("Alvo:Secrets:Values");
    }

    [Fact]
    public async Task A_present_required_secret_lets_the_boot_finish()
    {
        await using var alvo = await StartAsync(alvo => alvo.AddRequiredSecret(Present));

        alvo.Services.GetRequiredService<AlvoBootState>().Phase.ShouldBe(AlvoBootPhase.Ready);
    }

    [Fact]
    public async Task An_empty_required_secret_refuses_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartAsync(
            alvo => alvo.AddRequiredSecret("blank-secret").Services.Configure<AlvoSecretOptions>(options => options.Values["blank-secret"] = string.Empty)));

        refusal.Message.ShouldContain("'blank-secret'");
    }

    [Fact]
    public async Task No_secret_value_reaches_a_refusal_a_warning_or_a_log()
    {
        var logs = new SecretLogCapture();
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartAsync(
            alvo => alvo.AddRequiredSecret(Present).AddRequiredSecret("missing-secret"), logs));
        await using var alvo = await StartAsync(alvo => alvo.AddRequiredSecret(Present), logs);

        var applyRefusal = await Should.ThrowAsync<DescriptorValidationException>(() => ApplyAsync(alvo, WithSecretRef(PastedSecret)));
        applyRefusal.Result.Errors.ShouldContain(error => error.Path.TrimStart('#') == "/webhooks/endpoints/erp/secretRef");
        await ApplyAsync(alvo, WithSecretRef("not-yet-stored"));

        string[] seen = [refusal.ToString(), applyRefusal.ToString(), .. logs.Lines];
        seen.ShouldAllBe(text => !text.Contains(PresentValue) && !text.Contains(PastedSecret));
        refusal.Message.ShouldContain("'missing-secret'");
    }

    private static Task<AlvoTestHost> StartAsync(Action<IAlvoBuilder> more, SecretLogCapture? logs = null) => AlvoTestHost.StartAsync(alvo =>
    {
        alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", $"{Desk}.alvo.json"));
        alvo.AddFunction<IssueInvoice>("issue-invoice");
        alvo.Services
            .AddSingleton(new InvoiceLog())
            .AddSingleton<MMLib.Alvo.Scheduling.IAlvoTenantCatalog>(new FixedTenants())
            .Configure<AlvoSecretOptions>(options => options.Values[Present] = PresentValue);
        if (logs is not null)
        {
            alvo.Services.AddSingleton<ILoggerProvider>(logs);
        }

        more(alvo);
    }, cancellationToken: Ct);

    private static string WithSecretRef(string secretRef)
    {
        var desk = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "descriptors", $"{Desk}.alvo.json")))!;
        desk["webhooks"]!["endpoints"]!["erp"]!["secretRef"] = secretRef;
        return desk.ToJsonString();
    }

    private static async Task ApplyAsync(AlvoTestHost alvo, string json)
    {
        using var scope = alvo.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAlvoContextAccessor>().Principal =
            new AlvoPrincipal { Context = AlvoTestHost.Caller("ops"), Scopes = new HashSet<ApiKeyScope>(), KeyId = Desk };
        var management = scope.ServiceProvider.GetRequiredService<IAlvoManagement>();
        var current = await management.GetDescriptorAsync(Desk, Ct);

        (await management.ApplyDescriptorAsync(Desk, new ManagementApplyRequest(json, current.Revision), Ct)).Applied.ShouldBeTrue();
    }

    /// <summary>Keeps every rendered log line, exception included.</summary>
    private sealed class SecretLogCapture : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        internal IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _lines.Enqueue($"{formatter(state, exception)} {exception}");
        }

        public void Dispose()
        {
        }
    }
}
