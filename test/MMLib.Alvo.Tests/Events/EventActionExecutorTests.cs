using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using System.Diagnostics.Metrics;
using System.Net;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The action executor: a <c>webhook</c> POST and an <c>email</c> through the mail port, both driven off
/// templates that were compiled when the descriptor was applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every hook here is compiled through <see cref="PolicyCatalog.TryBuild"/>, never hand-built.</b> A
/// hand-built <c>CompiledAction</c> would let a fact assert that the executor renders a template this suite
/// wrote, rather than the template the descriptor declared and the compiler accepted — and the slot keys the
/// two sides agree on are exactly the kind of thing that silently stops matching. The one exception is the
/// unreachable-action arm, which no descriptor can produce and which therefore has to be built by hand.
/// </para>
/// <para>
/// <b>The transport is stubbed, and every fact asserts the body rather than the call.</b> A test that stubs
/// an <see cref="HttpMessageHandler"/> and asserts "it was called" passes a body that is complete nonsense,
/// so each delivery fact pins the bytes that went out — the round-tripped envelope, or the exact rendered
/// string — plus the URL they went to, which is the part a mis-resolved endpoint would break.
/// </para>
/// </remarks>
public sealed class EventActionExecutorTests : IDisposable
{
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;

    /// <summary>Builds the one logger pipeline every fact in this class reads, providers included.</summary>
    /// <remarks>
    /// A real <see cref="LoggerFactory"/> rather than the <see cref="ILogger"/> seam, so the facts run through
    /// the source-generated <c>LoggerMessage</c> delegates the product actually writes through — a fact over an
    /// <see cref="ILogger"/> substitute would pass on any wording, including none.
    /// </remarks>
    public EventActionExecutorTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    /// <inheritdoc/>
    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task A_webhook_action_posts_the_canonical_envelope_when_no_payload_is_declared()
    {
        var receiver = new RecordingWebhookReceiver();
        var @event = SampleEvent(Record(("title", "Big deal")));

        await Subject(receiver).ExecuteAsync(WebhookHook(), @event, Cancellation);

        AlvoEventJson.Read(receiver.Bodies.ShouldHaveSingleItem()).ShouldBe(@event);
    }

    [Fact]
    public async Task A_webhook_action_posts_its_rendered_template_when_one_is_declared()
    {
        var receiver = new RecordingWebhookReceiver();

        await Subject(receiver).ExecuteAsync(
            WebhookHook(payload: "{{new.title}}"),
            SampleEvent(Record(("title", "Big deal"))),
            Cancellation);

        receiver.Bodies.ShouldHaveSingleItem().ShouldBe("Big deal");
    }

    /// <summary>
    /// The delivery goes to the URL the descriptor declared for the named endpoint — resolved when the hook
    /// was compiled, so the URL, the condition and the templates all come from one apply.
    /// </summary>
    /// <remarks>
    /// There is no primed <em>descriptor</em> at run time, only the primed catalog, so an endpoint looked up
    /// at delivery would need a second independently primed holder — which is how an action comes to post one
    /// apply's URL while rendering another apply's template.
    /// </remarks>
    [Fact]
    public async Task A_webhook_action_posts_to_the_url_its_endpoint_declared()
    {
        var receiver = new RecordingWebhookReceiver();

        await Subject(receiver).ExecuteAsync(WebhookHook(), SampleEvent(), Cancellation);

        receiver.Targets.ShouldHaveSingleItem().ShouldBe(new Uri(EndpointUrl));
    }

    /// <summary>
    /// The delivery is resolved from the <em>named</em> client, which is the seam a host configures the
    /// handler, the timeout and any resilience on.
    /// </summary>
    [Fact]
    public async Task A_webhook_action_delivers_through_the_named_http_client_a_host_configures()
    {
        var receiver = new RecordingWebhookReceiver();
        var clients = new StubHttpClientFactory(receiver);

        await Subject(receiver, clients: clients).ExecuteAsync(WebhookHook(), SampleEvent(), Cancellation);

        clients.RequestedNames.ShouldHaveSingleItem().ShouldBe(WebhookDelivery.HttpClientName);
    }

    [Fact]
    public async Task An_email_action_renders_its_recipient_subject_and_body_from_the_envelope()
    {
        var mail = new RecordingEmailSender();

        await Subject(mail: mail).ExecuteAsync(
            EmailHook(to: "{{new.owner_email}}", subject: "Deal won: {{new.title}}", body: "{{new.amount}}"),
            SampleEvent(Record(("owner_email", "o@x.z"), ("title", "Big deal"), ("amount", 1200m))),
            Cancellation);

        var sent = mail.Messages.ShouldHaveSingleItem();
        sent.To.ShouldBe("o@x.z");
        sent.Subject.ShouldBe("Deal won: Big deal");
        sent.Body.ShouldBe("1200");
    }

    /// <summary>
    /// <c>email.to</c> is a plain-string sugar slot, so a hard-coded address is a legitimate recipient rather
    /// than a template that failed to interpolate.
    /// </summary>
    [Fact]
    public async Task A_literal_recipient_with_no_placeholder_is_a_legitimate_address()
    {
        var mail = new RecordingEmailSender();

        await Subject(mail: mail).ExecuteAsync(EmailHook(to: "ops@example.com"), SampleEvent(), Cancellation);

        mail.Messages.ShouldHaveSingleItem().To.ShouldBe("ops@example.com");
    }

    /// <summary>
    /// A failure must reach the caller, because the dispatcher's release-and-retry is the only thing that makes
    /// delivery at-least-once. An executor that logged and returned would turn every transient 503 into a
    /// silently dropped event.
    /// </summary>
    /// <remarks>
    /// <b>Every refusal is treated the same, and that is the decision.</b> A 500, a 404 and a 503 all throw:
    /// nothing at delivery time can tell a permanently wrong endpoint from one whose deployment is thirty
    /// seconds from finishing, and a per-status "permanent" verdict would need a dead-letter queue to put the
    /// abandoned event in. The bound is the attempt ceiling on the outbox claim instead.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_refused_delivery_throws_so_the_dispatcher_can_retry_it(HttpStatusCode status)
    {
        var receiver = new RecordingWebhookReceiver { Status = status };

        await Should.ThrowAsync<HttpRequestException>(
            () => Subject(receiver).ExecuteAsync(WebhookHook(), SampleEvent(), Cancellation));
    }

    /// <summary>
    /// A timeout is a <b>failure</b>, not a cancellation — so a slow endpoint is retried rather than read as
    /// the host shutting down and quietly ending the pump.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient"/> reports its own timeout as an <see cref="OperationCanceledException"/>, which
    /// is the same type shutdown raises. Leaving the two indistinguishable is how a dispatcher stops on a slow
    /// receiver.
    /// </remarks>
    [Fact]
    public async Task A_delivery_that_times_out_is_a_failure_rather_than_a_cancellation()
    {
        var receiver = new RecordingWebhookReceiver
        {
            Throws = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException()),
        };

        var failure = await Should.ThrowAsync<TimeoutException>(
            () => Subject(receiver).ExecuteAsync(WebhookHook(), SampleEvent(), Cancellation));

        failure.Message.ShouldContain(EndpointName);
    }

    /// <summary>
    /// <b>A slow endpoint's URL never reaches a log line — only the endpoint's <em>name</em> does.</b> This build
    /// reads no <c>secretRef</c> and sends no signature, so a secret embedded in the URL is the only
    /// authentication an author has, and the dispatcher logs a failed attempt at Warning with the exception
    /// attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The absence is asserted only after the same run has proved the URL was present.</b> The secret-shaped
    /// segment is checked in the URL the delivery was actually posted to first, so "not in the log" cannot pass
    /// because the value was never in play — which is the way an absence fact goes vacuous. The
    /// <em>failure</em> path is the one that mattered: the action log deliberately carries no URL, and until the
    /// timeout message was reworded, the failure path carried the whole one, scheme, path and query included.
    /// </para>
    /// <para>
    /// <b>What is read is the message <em>and</em> the attached exception</b>, because that is what a log
    /// pipeline ships: <c>ActionFailed</c>'s own template names the event and the attempt, and the endpoint
    /// arrives only through the exception the dispatcher passes it. Asserting over the rendered message alone
    /// would have declared the URL absent while it was two fields away.
    /// </para>
    /// <para>
    /// The whole log is read, at every level and through the real <c>LoggerMessage</c> delegates, rather than one
    /// line: a rule about what this subsystem does not disclose is a property of the set of lines it writes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task No_log_line_carries_a_webhook_url_that_could_be_a_secret()
    {
        var receiver = new RecordingWebhookReceiver
        {
            Throws = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException()),
        };
        var hook = SecretUrlHook();

        var failure = await Should.ThrowAsync<TimeoutException>(
            () => Subject(receiver).ExecuteAsync(hook, SampleEvent(), Cancellation));
        EventLog.ActionFailed(
            _loggers.CreateLogger<OutboxDispatcher>(), Guid.NewGuid(), "entity.deals.updated", 1, failure);

        receiver.Targets.ShouldHaveSingleItem().AbsoluteUri.ShouldContain(
            SecretSegment, Case.Sensitive, "the positive control: the secret really was on the wire this run");

        var shipped = Shipped();
        shipped.ShouldNotBeEmpty("the dispatcher's own failure line must have been written");
        shipped.ShouldAllBe(line => !line.Contains(SecretSegment, StringComparison.Ordinal));
        shipped.ShouldAllBe(line => !line.Contains(SecretUrl, StringComparison.Ordinal));
        shipped.ShouldContain(line => line.Contains(EndpointName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half: a cancellation the <em>caller</em> asked for stays a cancellation, so shutdown is not
    /// reported as a delivery failure and retried.
    /// </summary>
    [Fact]
    public async Task A_delivery_cancelled_by_a_shutdown_stays_a_cancellation()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => Subject().ExecuteAsync(WebhookHook(), SampleEvent(), shutdown.Token));
    }

    /// <summary>
    /// Decision D7, named rather than left in a paragraph: the envelope carries the <b>unmasked</b> post-image,
    /// so a <c>hidden</c> field reaches a descriptor-declared endpoint.
    /// </summary>
    /// <remarks>
    /// Accepted in F3 because an after-hook condition reading <c>old.commission_note</c> or
    /// <c>changed(commission_note)</c> must see every field — <c>hidden</c> is a per-caller read mask, not a
    /// data classification — and because the endpoint is declared in the same descriptor by the same author as
    /// the <c>hidden</c> rule, never caller-supplied. Per-endpoint field projection is filed as #152. This fact
    /// exists so the disclosure is a decision on the record: if it ever becomes wrong, this is the test that has
    /// to change, deliberately.
    /// </remarks>
    [Fact]
    public async Task A_webhook_receives_the_unmasked_record_and_that_is_documented()
    {
        var receiver = new RecordingWebhookReceiver();

        await Subject(receiver).ExecuteAsync(
            WebhookHook(), SampleEvent(Record(("commission_note", "12%"))), Cancellation);

        AlvoEventJson.Read(receiver.Bodies.ShouldHaveSingleItem())
            .Data.Record.ShouldNotBeNull()["commission_note"].ShouldBe("12%");
    }

    /// <summary>
    /// The execution-log entry names the hook, the action type and the event — and <b>never</b> a rendered
    /// value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delivered body carries the unmasked record (see
    /// <see cref="A_webhook_receives_the_unmasked_record_and_that_is_documented"/>), so logging the rendered
    /// value would take a <c>hidden</c> field out of the one place the design accepted it going — a
    /// descriptor-declared endpoint, chosen by the same author as the <c>hidden</c> rule — and put it into
    /// whatever ships logs, which nobody declared. The event id is the join key: the payload is stored once, in
    /// the <c>alvo_outbox</c> row, under that table's retention rather than a log pipeline's.
    /// </para>
    /// <para>
    /// The absence assertion is not vacuous, because the same run proves the value <em>was</em> in the
    /// delivered body: it is present in this execution and simply not in the log line.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_action_log_names_the_hook_and_the_event_but_never_the_rendered_body()
    {
        var receiver = new RecordingWebhookReceiver();
        var @event = SampleEvent(Record(("commission_note", "12%")));

        await Subject(receiver).ExecuteAsync(
            WebhookHook(payload: "{{new.commission_note}}"), @event, Cancellation);

        receiver.Bodies.ShouldHaveSingleItem().ShouldBe("12%");
        var line = _logs.Entries.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldContain(HookPath);
        line.Message.ShouldContain("webhook");
        line.Message.ShouldContain(@event.Id.ToString());
        line.Message.ShouldNotContain("12%");
    }

    /// <summary>
    /// The entry means "this ran", not "this was attempted" — so a failed delivery leaves no execution-log
    /// entry behind, and a retry does not accumulate one entry per attempt.
    /// </summary>
    [Fact]
    public async Task An_action_that_failed_writes_no_execution_log_entry()
    {
        var receiver = new RecordingWebhookReceiver { Status = HttpStatusCode.ServiceUnavailable };

        await Should.ThrowAsync<HttpRequestException>(
            () => Subject(receiver).ExecuteAsync(WebhookHook(), SampleEvent(), Cancellation));

        _logs.Entries.ShouldBeEmpty();
    }

    /// <summary>
    /// The console provider is a <em>development</em> provider and says so, so nobody ships it believing mail
    /// is going out. There is no SMTP sender in this build and no mail service in the compose file.
    /// </summary>
    [Fact]
    public async Task The_console_sender_writes_the_whole_message_and_names_itself_a_dev_provider()
    {
        await new ConsoleEmailSender(_loggers.CreateLogger<ConsoleEmailSender>())
            .SendAsync(new AlvoMailMessage("o@x.z", "Deal won", "Big deal closed."), Cancellation);

        var line = _logs.Entries.ShouldHaveSingleItem().Message;
        line.ShouldContain("o@x.z");
        line.ShouldContain("Deal won");
        line.ShouldContain("Big deal closed.");
        line.ShouldContain("development");
    }

    /// <summary>
    /// The default arm of the action switch throws rather than doing nothing. It is unreachable from a
    /// descriptor — the other three action types are refused when one is applied — and exists so a
    /// <em>host</em>-built catalog cannot reach a silent no-op.
    /// </summary>
    [Fact]
    public async Task An_action_type_no_descriptor_can_carry_throws_rather_than_silently_doing_nothing()
    {
        var action = new FunctionAction { Name = "recalculate" };
        var hook = new CompiledAfterHook(
            HookPath,
            Condition: null,
            RequiredContext.None,
            new CompiledAction(
                action,
                new Dictionary<string, AlvoTemplate>(StringComparer.Ordinal),
                Endpoint: null,
                // Resolved rather than written as "function", so TypeName cannot drift from Action in the one
                // place a catalog is hand-built — which is the whole point of this fact.
                ActionType.NameOf(action)));

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => Subject().ExecuteAsync(hook, SampleEvent(), Cancellation));

        refusal.Message.ShouldContain("function");
        refusal.Message.ShouldContain(HookPath);
    }

    /// <summary>
    /// The three counters are published on <b>one</b> meter, under the names the subsystem documents.
    /// </summary>
    /// <remarks>
    /// A naming pin rather than a behavioural fact — the increments are the dispatcher's. It is here because a
    /// counter created on a second meter is silently unobserved by a listener subscribed to this one, so a
    /// criterion counting increments would read zero and be indistinguishable from a counter never touched.
    /// </remarks>
    [Fact]
    public void Every_event_counter_is_published_on_the_one_meter_under_its_documented_name()
    {
        Counter<long>[] counters = [AlvoEventMetrics.Dispatched, AlvoEventMetrics.Filtered, AlvoEventMetrics.Failed];

        counters.Select(counter => counter.Name).ShouldBe(AlvoEventMetrics.AllInstrumentNames);
        counters.ShouldAllBe(counter => counter.Meter.Name == AlvoEventMetrics.MeterName);
        AlvoEventMetrics.AllInstrumentNames.ShouldBe(
            ["alvo.events.dispatched", "alvo.events.filtered", "alvo.events.failed"]);
    }

    /// <summary>
    /// Everything the run would hand a log pipeline: each entry's rendered message together with the exception
    /// attached to it, which is where a delivery failure's own words travel.
    /// </summary>
    private IReadOnlyList<string> Shipped() =>
        [.. _logs.Entries.Select(entry => $"{entry.Message} {entry.Exception}")];

    private const string EndpointUrl = "https://example.test/hook";
    private const string EndpointName = "crm-sync";

    /// <summary>The path segment that stands in for the credential a Slack/Teams/Zapier webhook URL carries.</summary>
    private const string SecretSegment = "T00000000-B11111111-Kd7xQ2vSecretToken";

    private const string SecretUrl = $"https://hooks.example.test/services/{SecretSegment}";
    private const string TemplateName = "deal-won";
    private const string HookPath = "/entities/deals/hooks/afterUpdate/0";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private EventActionExecutor Subject(
        RecordingWebhookReceiver? receiver = null,
        IEmailSender? mail = null,
        StubHttpClientFactory? clients = null) =>
        new(new WebhookDelivery(clients ?? new StubHttpClientFactory(receiver ?? new RecordingWebhookReceiver())),
            mail ?? new RecordingEmailSender(),
            _loggers.CreateLogger<EventActionExecutor>());

    private static CompiledAfterHook WebhookHook(string? payload = null) =>
        Hook(new WebhookAction { Endpoint = EndpointName, Payload = payload });

    /// <summary>
    /// One hook compiled against an endpoint whose URL carries its credential in the path, which is how a
    /// Slack, Teams, Zapier or Make endpoint is actually shaped.
    /// </summary>
    private static CompiledAfterHook SecretUrlHook() =>
        Hook(new WebhookAction { Endpoint = EndpointName }, url: SecretUrl);

    private static CompiledAfterHook EmailHook(string to, string? subject = null, string? body = null) =>
        Hook(new EmailAction { Template = TemplateName, To = to }, subject, body);

    /// <summary>
    /// Compiles one hook through the real apply path, so every template and the endpoint come from the
    /// compiler rather than from this suite.
    /// </summary>
    private static CompiledAfterHook Hook(
        AutomationAction action, string? subject = null, string? body = null, string url = EndpointUrl)
    {
        PolicyCatalog.TryBuild(
            Descriptor(action, subject, body, url), Schema, CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue($"expected a clean build, got: {string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"))}");

        catalog.ShouldNotBeNull().TryGetEntity("deals", out var policy).ShouldBeTrue();
        return policy.AfterHooks.For(DataOperation.Update).ShouldHaveSingleItem();
    }

    private static AlvoDescriptor Descriptor(AutomationAction action, string? subject, string? body, string url) =>
        new()
        {
            ApiVersion = "alvo.dev/v1",
            Name = "test",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                ["deals"] = new()
                {
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                    Hooks = new EntityHooks { AfterUpdate = [new AfterHook { Action = action }] },
                },
            },
            Templates = new Dictionary<string, MessageTemplate>(StringComparer.Ordinal)
            {
                [TemplateName] = new() { Subject = subject, Body = body },
            },
            Webhooks = new Webhooks
            {
                Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
                {
                    [EndpointName] = new() { Url = url, SecretRef = "crm-sync-secret" },
                },
            },
        };

    private static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = "deals",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid },
                new FieldSchema { Name = "title", Type = FieldType.String, MaxLength = 200 },
                new FieldSchema { Name = "amount", Type = FieldType.Decimal },
                new FieldSchema { Name = "owner_email", Type = FieldType.String, MaxLength = 200 },
                new FieldSchema { Name = "commission_note", Type = FieldType.String, MaxLength = 200 },
            ],
        },
    ]);

    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal));

    /// <summary>
    /// One envelope with a fixed id and instant, so a round-trip fact compares values rather than identity.
    /// </summary>
    private static AlvoEvent SampleEvent(AlvoRecord? record = null) => new()
    {
        Id = Guid.Parse("019000aa-0000-7000-8000-0000000000d1"),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.deals.updated",
        Time = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero),
        Subject = "deals/019000aa-0000-7000-8000-0000000000ff",
        PartitionKey = "deals:019000aa-0000-7000-8000-0000000000ff",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "019000aa-0000-7000-8000-0000000000c0",
        Data = new AlvoEventData { Record = record ?? AlvoRecord.Empty, Changed = ["title"] },
    };

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that records what was posted and answers <see cref="Status"/> — no
    /// socket, so this suite stays in the fast ring while still exercising the whole delivery path.
    /// </summary>
    private sealed class RecordingWebhookReceiver : HttpMessageHandler
    {
        private readonly List<string> _bodies = [];
        private readonly List<Uri> _targets = [];

        /// <summary>The status every delivery is answered with.</summary>
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        /// <summary>What the transport throws instead of answering, when it throws.</summary>
        internal Exception? Throws { get; init; }

        /// <summary>Every posted body, in order.</summary>
        internal IReadOnlyList<string> Bodies => _bodies;

        /// <summary>Every URL posted to, in order.</summary>
        internal IReadOnlyList<Uri> Targets => _targets;

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            _targets.Add(request.RequestUri!);
            _bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return Throws is null ? new HttpResponseMessage(Status) : throw Throws;
        }
    }

    /// <summary>An <see cref="IHttpClientFactory"/> over one handler, recording the names asked for.</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly List<string> _requestedNames = [];

        /// <summary>Every client name resolved through this factory, in order.</summary>
        internal IReadOnlyList<string> RequestedNames => _requestedNames;

        /// <inheritdoc/>
        public HttpClient CreateClient(string name)
        {
            _requestedNames.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    /// <summary>An <see cref="IEmailSender"/> that keeps every message instead of sending it.</summary>
    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly List<AlvoMailMessage> _messages = [];

        /// <summary>Every message handed to this sender, in order.</summary>
        internal IReadOnlyList<AlvoMailMessage> Messages => _messages;

        /// <inheritdoc/>
        public Task SendAsync(AlvoMailMessage message, CancellationToken cancellationToken = default)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
