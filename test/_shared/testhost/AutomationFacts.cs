using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Functions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Scheduling;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>What the automation facts' function saw, and what it is told to do.</summary>
public sealed class InvoiceLog
{
    public ConcurrentQueue<(Guid OrderId, decimal Amount, TenantId? Tenant, string Subscriber)> Issued { get; } = new();

    public bool Skip { get; set; }
}

/// <summary>The input of the automation facts' function.</summary>
public sealed record InvoiceInput(Guid OrderId, decimal Amount);

/// <summary>Records each invoice it is asked to issue — or skips, when told to.</summary>
public sealed class IssueInvoice(InvoiceLog log) : IAlvoFunction<InvoiceInput>
{
    public Task InvokeAsync(InvoiceInput input, AlvoFunctionContext context, CancellationToken cancellationToken)
    {
        if (log.Skip)
        {
            throw new AlvoFunctionSkippedException("nothing to invoice");
        }

        log.Issued.Enqueue((input.OrderId, input.Amount, context.Caller.Tenant, context.Event.Type));
        return Task.CompletedTask;
    }
}

/// <summary>Captures every mail the automation facts send.</summary>
public sealed class CapturedMail : IEmailSender
{
    public ConcurrentQueue<AlvoMailMessage> Sent { get; } = new();

    public Task SendAsync(AlvoMailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Answers webhook posts from a script of statuses — then 200 — and keeps every body.</summary>
internal sealed class ScriptedReceiver(params HttpStatusCode[] script) : HttpMessageHandler
{
    private readonly ConcurrentQueue<HttpStatusCode> _script = new(script);
    private readonly ConcurrentQueue<string> _bodies = new();

    internal IReadOnlyList<string> Bodies => [.. _bodies];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(_script.TryDequeue(out var status) ? status : HttpStatusCode.OK);
    }
}

/// <summary>Records every log line, rendered, so a fact can say what never reached one.</summary>
internal sealed class AutomationLogCapture : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<(string? Name, string Text)> _lines = new();

    internal IReadOnlyList<(string? Name, string Text)> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _lines.Enqueue((eventId.Name, $"{formatter(state, exception)} {exception}"));
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// The automation block and every descriptor action as its own delivery, over the real pipeline (spec 2026-10-14 §7 HX-15), on
/// whichever engine the deriving class supplies — the SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in
/// <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
/// <remarks>The clock starts at 01:00 UTC, so the <c>0 2 * * *</c> rule falls due one hour in.</remarks>
public abstract partial class AutomationFacts
{
    protected const string OrderDesk = "order-desk";
    protected const string Webhook = "/entities/orders/hooks/afterCreate#";
    protected const string InvoiceRule = "/automation/invoice-on-create/actions#";
    protected const string NightlyRule = "/automation/nightly-close/actions#";
    protected const string NightlySchedule = "/automation/nightly-close#";

    private static readonly DateTimeOffset _oneAm = new(2026, 11, 7, 1, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected FakeTimeProvider Time { get; } = new(_oneAm);

    protected TenantId TenantA { get; } = new(Guid.NewGuid());

    protected TenantId TenantB { get; } = new(Guid.NewGuid());

    protected InvoiceLog Invoices { get; } = new();

    protected CapturedMail Mail { get; } = new();

    [Fact]
    public async Task A_failing_webhook_action_retries_alone_and_the_email_of_the_same_event_is_sent_once()
    {
        var receiver = new ScriptedReceiver(HttpStatusCode.ServiceUnavailable);
        await using var alvo = await StartAsync(receiver);
        await CreateOrderAsync(alvo, TenantA, "A-1", total: 10m);

        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromMinutes(5));
        await alvo.DrainEventsAsync(Ct);

        receiver.Bodies.Count.ShouldBe(2);
        Mail.Sent.Count.ShouldBe(1);
        var webhook = (await DeliveriesAsync(alvo, Webhook)).Single(delivery => delivery.Kind == "action" && delivery.Attempts == 2);
        webhook.Status.ShouldBe("succeeded");
        var all = await DeliveriesAsync(alvo, Webhook);
        all.Count.ShouldBe(2, "the webhook and the email of one point are two deliveries");
        all.Single(delivery => delivery.Id != webhook.Id).ShouldSatisfyAllConditions(
            email => email.Status.ShouldBe("succeeded"),
            email => email.Attempts.ShouldBe(1));
    }

    [Fact]
    public async Task An_automation_rule_runs_its_function_with_the_checked_input()
    {
        await using var alvo = await StartAsync();
        var order = await CreateOrderAsync(alvo, TenantA, "A-1", total: 12.5m);

        await alvo.DrainEventsAsync(Ct);

        var issued = Invoices.Issued.ShouldHaveSingleItem();
        (issued.OrderId, issued.Amount, issued.Tenant, issued.Subscriber).ShouldBe((order, 12.5m, (TenantId?)TenantA, "entity.orders.created"));
        var delivery = (await DeliveriesAsync(alvo, InvoiceRule)).ShouldHaveSingleItem();
        (delivery.Kind, delivery.Status).ShouldBe(("function", "succeeded"));
    }

    [Fact]
    public async Task A_function_that_skips_ends_its_delivery_succeeded()
    {
        Invoices.Skip = true;
        await using var alvo = await StartAsync();
        await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m);

        await alvo.DrainEventsAsync(Ct);

        Invoices.Issued.ShouldBeEmpty();
        (await DeliveriesAsync(alvo, InvoiceRule)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_function_input_the_type_cannot_bind_is_dead_without_its_values_in_the_log()
    {
        var logs = new AutomationLogCapture();
        await using var alvo = await StartAsync(logs: logs);
        var order = await CreateOrderAsync(alvo, TenantA, "SECRET-NUMBER", total: null, note: "secret-note");

        await alvo.DrainEventsAsync(Ct);

        Invoices.Issued.ShouldBeEmpty();
        var delivery = (await DeliveriesAsync(alvo, InvoiceRule)).ShouldHaveSingleItem();
        (delivery.Status, delivery.Attempts, delivery.LastErrorType).ShouldBe(("dead", 1, "function-input-invalid"));
        var refusal = logs.Lines.Single(line => line.Name == "FunctionInputInvalid").Text;
        refusal.ShouldContain("issue-invoice");
        logs.Lines.ShouldAllBe(line => !line.Text.Contains("SECRET-NUMBER") && !line.Text.Contains("secret-note"));
        _ = order;
    }

    [Fact]
    public async Task A_cron_rule_fires_its_actions_once_per_occurrence()
    {
        await using var alvo = await StartAsync();
        await alvo.DrainEventsAsync(Ct);
        (await DeliveriesAsync(alvo, NightlyRule)).ShouldBeEmpty();

        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        var deliveries = await DeliveriesAsync(alvo, NightlyRule);
        deliveries.Count.ShouldBe(2, "one per tenant the catalog lists");
        deliveries.ShouldAllBe(delivery => delivery.Kind == "action");
        deliveries.Select(delivery => delivery.TenantId).ShouldBe([TenantA.ToString(), TenantB.ToString()], ignoreOrder: true);
        (await DeliveriesAsync(alvo, NightlySchedule)).ShouldBeEmpty("the trigger-only schedule mints its event and is never delivered to");
        (await DeliveriesAsync(alvo, "/automation/nightly-close")).Select(delivery => delivery.EventId).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_cron_rule_added_by_an_apply_fires_without_a_restart()
    {
        await using var alvo = await StartAsync(descriptor: WithoutNightly());
        await alvo.DrainEventsAsync(Ct);

        await ApplyAsync(alvo, File.ReadAllText(DescriptorPath()));
        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        (await DeliveriesAsync(alvo, NightlyRule)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Editing_a_hook_between_boots_cancels_its_retrying_delivery_and_the_boot_starts()
    {
        var database = await DatabaseAsync();
        var receiver = new ScriptedReceiver(HttpStatusCode.ServiceUnavailable);
        await using (var first = await StartAsync(receiver, database: database))
        {
            await CreateOrderAsync(first, TenantA, "A-1", total: 1m);
            await first.DrainEventsAsync(Ct);
        }

        var stale = (await DeliveriesAsync(database, Webhook)).Single(delivery => delivery.Status == "retrying");
        await using var second = await StartAsync(receiver, database: database, descriptor: WithEditedWebhook());
        Time.Advance(TimeSpan.FromMinutes(5));
        await second.DrainEventsAsync(Ct);

        receiver.Bodies.Count.ShouldBe(1, "the edited action never runs for the old subscriber's pending row");
        (await DeliveriesAsync(database, Webhook)).Single(delivery => delivery.Subscriber == stale.Subscriber).Status.ShouldBe("cancelled");
        (await DeliveryRows.OperationRowsAsync(database.Connect, Ct)).ShouldHaveSingleItem().Target.ShouldBe(stale.Subscriber);
    }

    protected abstract Task<AutomationDatabase> DatabaseAsync();

    private protected async Task<AlvoTestHost> StartAsync(
        HttpMessageHandler? webhooks = null,
        AutomationDatabase? database = null,
        string? descriptor = null,
        AutomationLogCapture? logs = null,
        bool failAfterCommit = false)
    {
        var used = database ?? await DatabaseAsync();
        var path = descriptor is null ? DescriptorPath() : await WriteDescriptorAsync(descriptor);

        return await AlvoTestHost.StartAsync(
            alvo => Configure(alvo, used, path, logs, failAfterCommit), Time, webhooks ?? new ScriptedReceiver(), Ct);
    }

    private void Configure(IAlvoBuilder alvo, AutomationDatabase database, string path, AutomationLogCapture? logs, bool failAfterCommit)
    {
        database.Use(alvo);
        if (failAfterCommit)
        {
            FailFirstCommit(alvo.Services);
        }

        alvo.FromDescriptor(path);
        alvo.AddFunction<IssueInvoice>("issue-invoice");
        alvo.Services
            .AddSingleton(Invoices)
            .AddSingleton<IEmailSender>(Mail)
            .AddSingleton<IAlvoTenantCatalog>(new FixedTenants(TenantA, TenantB));
        if (logs is not null)
        {
            alvo.Services.AddSingleton<ILoggerProvider>(logs);
        }
    }

    /// <summary>Wraps the provider's unit in <see cref="FailingAfterCommit"/>, for the one fact about a lost answer.</summary>
    private static void FailFirstCommit(IServiceCollection services)
    {
        var registered = services.Last(service => service.ServiceType == typeof(IAlvoTransactions));
        services.Remove(registered);
        services.AddSingleton<IAlvoTransactions>(provider => new FailingAfterCommit(Resolve(registered, provider)));
    }

    private static IAlvoTransactions Resolve(ServiceDescriptor registered, IServiceProvider provider) =>
        (IAlvoTransactions)(registered.ImplementationInstance
            ?? registered.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!));

    protected static string DescriptorPath() => Path.Combine(AppContext.BaseDirectory, "descriptors", $"{OrderDesk}.alvo.json");

    private static async Task<string> WriteDescriptorAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-automation-{Guid.NewGuid():N}.alvo.json");
        await File.WriteAllTextAsync(path, json, Ct);
        return path;
    }

    protected static JsonObject Desk() => JsonNode.Parse(File.ReadAllText(DescriptorPath()))!.AsObject();

    private static string WithoutNightly()
    {
        var desk = Desk();
        desk["automation"]!.AsObject().Remove("nightly-close");
        return desk.ToJsonString();
    }

    private static string WithEditedWebhook(string condition = "new.number != ''")
    {
        var desk = Desk();
        desk["entities"]!["orders"]!["hooks"]!["afterCreate"]![0]!["condition"] = condition;
        return desk.ToJsonString();
    }

    protected static async Task<Guid> CreateOrderAsync(AlvoTestHost alvo, TenantId tenant, string number, decimal? total, string? note = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["number"] = number,
            ["total"] = total,
            ["note"] = note,
            ["tenant_id"] = tenant.Value,
        };
        var created = await alvo.Data.CreateAsync("orders", values, Staff(tenant), cancellationToken: Ct);

        return (Guid)created["id"]!;
    }

    protected static AlvoContext Staff(TenantId tenant) => AlvoTestHost.Caller("staff") with { Tenant = tenant };

    protected static async Task<int> ApplyAsync(AlvoTestHost alvo, string json)
    {
        using var scope = alvo.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAlvoContextAccessor>().Principal =
            new AlvoPrincipal { Context = AlvoTestHost.Caller("ops"), Scopes = new HashSet<ApiKeyScope>(), KeyId = OrderDesk };
        var management = scope.ServiceProvider.GetRequiredService<IAlvoManagement>();
        var current = await management.GetDescriptorAsync(OrderDesk, Ct);

        var applied = await management.ApplyDescriptorAsync(OrderDesk, new ManagementApplyRequest(json, current.Revision), Ct);
        applied.Applied.ShouldBeTrue();
        return applied.Revision;
    }

    protected static async Task<List<StoredDelivery>> DeliveriesAsync(AlvoTestHost alvo, string subscriberPrefix) =>
        [.. (await alvo.Services.GetRequiredService<IDeliveryStore>().ListAsync(new DeliveryFilter { Limit = 500 }, Ct))
            .Where(delivery => delivery.Subscriber.StartsWith(subscriberPrefix, StringComparison.Ordinal))];

    private protected static async Task<List<DeliveryRow>> DeliveriesAsync(AutomationDatabase database, string subscriberPrefix) =>
        [.. (await DeliveryRows.ReadAsync(database.Connect, Ct)).Where(row => row.Subscriber.StartsWith(subscriberPrefix, StringComparison.Ordinal))];

    protected sealed record AutomationDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
