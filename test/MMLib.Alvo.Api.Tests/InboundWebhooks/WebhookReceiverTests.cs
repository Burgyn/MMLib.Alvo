using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.InboundWebhooks.Internal;
using MMLib.Alvo.Secrets;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Api.Tests.InboundWebhooks;

/// <summary>
/// Inbound webhooks over HTTP (spec 2026-10-14 §4.9, acceptance (4)): verified, inside the window, once per
/// <c>(pattern, webhook-id)</c>, run as the system in the configured tenant, and forgotten after seven days.
/// </summary>
public sealed class WebhookReceiverTests : IDisposable
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string TenantText = "7b8f0c4e-2b8e-4f6f-9b1e-0c7a2d1f3e55";
    private static readonly TenantId _tenant = new(Guid.Parse(TenantText));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
    private readonly Probe _probe = new();
    private readonly LogCapture _logs = new();

    public void Dispose() => _logs.Dispose();

    [Fact]
    public async Task A_signed_delivery_runs_the_handler_as_the_system_in_the_configured_tenant()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_1", "{\"paid\":true}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (body, caller) = _probe.Seen.ShouldHaveSingleItem();
        body.ShouldBe("{\"paid\":true}", "the handler binds the very bytes that were verified");
        caller.ActorKind.ShouldBe(AlvoActorKind.System);
        caller.Tenant.ShouldBe(_tenant);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public async Task A_timestamp_six_minutes_off_is_refused(int minutes)
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_2", "{}", _time.GetUtcNow().AddMinutes(minutes));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Unauthenticated);
        response.Headers.WwwAuthenticate.ShouldBeEmpty("Standard Webhooks has no challenge to name");
        _probe.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_repeated_webhook_id_on_one_receiver_does_not_run_the_handler_again()
    {
        await using var world = await StartAsync();

        using var first = await SendAsync(world, "/hooks/payments", "msg_3", "{}");
        using var repeat = await SendAsync(world, "/hooks/payments", "msg_3", "{}");
        using var elsewhere = await SendAsync(world, "/hooks/refunds", "msg_3", "{}");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK, "a handled id is acknowledged, so the sender stops retrying");
        elsewhere.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(2, "dedup is per receiver: the second receiver runs the same id once");
    }

    [Fact]
    public async Task A_bad_signature_is_401_and_records_nothing()
    {
        await using var world = await StartAsync();

        using var forged = await SendAsync(world, "/hooks/payments", "msg_4", "{}", signature: "v1,Zm9yZ2Vk");
        using var genuine = await SendAsync(world, "/hooks/payments", "msg_4", "{}");

        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        genuine.StatusCode.ShouldBe(HttpStatusCode.OK, "an unverified id was never recorded, so the genuine delivery runs");
        _probe.Seen.Count.ShouldBe(1);
        _probe.TenantsResolved.ShouldBe(1, "the tenant is resolved only for a verified delivery");
    }

    [Fact]
    public async Task A_failing_handler_gives_its_id_back_so_the_sender_s_retry_runs()
    {
        _probe.FailNext = true;
        await using var world = await StartAsync();

        using var failed = await SendAsync(world, "/hooks/payments", "msg_5", "{}");
        using var retried = await SendAsync(world, "/hooks/payments", "msg_5", "{}");

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await failed.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Internal, "a receiver's failure is answered by Alvo, as a command's is");
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_handler_answering_non_2xx_gives_its_id_back_so_the_sender_s_retry_runs()
    {
        _probe.StatusNext = StatusCodes.Status503ServiceUnavailable;
        await using var world = await StartAsync();

        using var refused = await SendAsync(world, "/hooks/payments", "msg_13", "{}");
        using var retried = await SendAsync(world, "/hooks/payments", "msg_13", "{}");

        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK, "a 503 never records the id, or the retry would be acknowledged unrun");
        _probe.Runs.ShouldBe(2);
    }

    /// <summary>
    /// A release that fails too is logged; it never replaces the handler's own exception, which is the one the host's
    /// exception handler records — the id frees itself when its lease runs out.
    /// </summary>
    [Fact]
    public async Task A_release_failing_after_the_handler_threw_never_hides_the_handlers_exception()
    {
        _probe.FailNext = true;
        await using var world = await StartAsync(ledger: new ReleaseFailingLedger());

        using var failed = await SendAsync(world, "/hooks/payments", "msg_21", "{}");

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _logs.Exceptions.ShouldContain(exception => exception.Message == "the handler failed");
        _logs.Exceptions.ShouldContain(exception => exception.Message == ReleaseFailingLedger.Failure, "the failed release is logged");
        _logs.Exceptions.Count(exception => exception.Message == ReleaseFailingLedger.Failure).ShouldBe(1, "and only logged, never rethrown");
    }

    [Fact]
    public async Task A_release_failing_after_a_non_2xx_answer_keeps_the_handlers_answer()
    {
        _probe.StatusNext = StatusCodes.Status503ServiceUnavailable;
        await using var world = await StartAsync(ledger: new ReleaseFailingLedger());

        using var refused = await SendAsync(world, "/hooks/payments", "msg_22", "{}");

        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _logs.Exceptions.ShouldContain(exception => exception.Message == ReleaseFailingLedger.Failure);
    }

    /// <summary>
    /// Standard Webhooks signs <c>{id}.{timestamp}.{body}</c> with the timestamp header as it was sent, so a sender that
    /// zero-pads it still verifies — the signed content is never a re-formatted number.
    /// </summary>
    [Fact]
    public async Task A_zero_padded_timestamp_verifies_over_the_header_as_sent()
    {
        await using var world = await StartAsync();
        var padded = "0" + Now();
        var signedAsSent = "v1," + Convert.ToBase64String(HMACSHA256.HashData(
            StandardWebhookSignature.Keys(Secret)[0], Encoding.UTF8.GetBytes($"msg_23.{padded}.{{}}")));

        using var response = await SendAsync(world, "/hooks/payments", "msg_23", "{}", signature: signedAsSent, timestampText: padded);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_secret_shorter_than_24_bytes_is_a_500_whose_error_log_says_why_and_names_no_key()
    {
        var shortKey = Enumerable.Repeat((byte)7, 23).ToArray();
        await using var world = await StartAsync(secret: "whsec_" + Convert.ToBase64String(shortKey));
        var signed = StandardWebhookSignature.Sign(shortKey, "msg_14", Now(), "{}"u8);

        using var response = await SendAsync(world, "/hooks/payments", "msg_14", "{}", signature: signed);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _probe.Runs.ShouldBe(0);
        var error = _logs.Messages(LogLevel.Error).ShouldHaveSingleItem();
        error.ShouldContain("pay-provider");
        error.ShouldContain("24 bytes");
        error.ShouldNotContain(Convert.ToBase64String(shortKey));
    }

    [Fact]
    public async Task A_missing_secret_is_a_500_that_names_nothing()
    {
        await using var world = await StartAsync(secret: null);

        using var response = await SendAsync(world, "/hooks/payments", "msg_6", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.ReadTextAsync()).ShouldNotContain("pay-provider");
    }

    [Fact]
    public async Task An_empty_secret_is_a_500_never_a_key_a_forger_can_sign_with()
    {
        await using var world = await StartAsync(secret: "whsec_");
        var forged = StandardWebhookSignature.Sign([], "msg_11", Now(), "{}"u8);

        using var response = await SendAsync(world, "/hooks/payments", "msg_11", "{}", signature: forged);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _probe.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_7", "{\"pad\":\"" + new string('x', 2_000) + "\"}");

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _probe.Seen.ShouldBeEmpty();
    }

    /// <summary>A body with no <c>Content-Length</c> is capped while it is read, not buffered to the server's own limit.</summary>
    [Fact]
    public async Task A_body_over_the_limit_without_a_length_is_413()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_15", Padded(1_025), unsized: true);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.UnreadableRequest);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_body_exactly_at_the_limit_without_a_length_is_handled()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_16", Padded(1_024), unsized: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.ShouldHaveSingleItem().Body.Length.ShouldBe(1_024);
    }

    /// <summary>Ruling 55: a server limit below the receiver's is raised to it — the receiver's own cap is the one that applies.</summary>
    [Fact]
    public async Task A_writable_server_limit_below_the_receiver_s_is_raised_to_it()
    {
        _probe.ServerLimit = new ServerBodyLimit(512);
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_17", Padded(800), unsized: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.ServerLimit.MaxRequestBodySize.ShouldBe(1_024);
    }

    [Fact]
    public async Task A_server_limit_above_the_receiver_s_is_never_lowered()
    {
        _probe.ServerLimit = new ServerBodyLimit(4_096);
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_18", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.ServerLimit.MaxRequestBodySize.ShouldBe(4_096);
    }

    /// <summary>A server limit the receiver cannot raise still answers the receiver's own 413, never the host's 500.</summary>
    [Fact]
    public async Task A_read_only_server_limit_the_body_crosses_is_the_receiver_s_own_413()
    {
        _probe.ServerLimit = new ServerBodyLimit(512, readOnly: true);
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_19", Padded(800), unsized: true);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.UnreadableRequest);
        _probe.Runs.ShouldBe(0);
    }

    /// <summary>The receiver answers its own 413 even in a host that maps no exception handler for Alvo to answer through.</summary>
    [Fact]
    public async Task A_read_only_server_limit_is_the_receiver_s_413_in_a_host_with_no_exception_handler()
    {
        _probe.ServerLimit = new ServerBodyLimit(512, readOnly: true);
        await using var world = await StartAsync(answeredByAlvo: false);

        using var response = await SendAsync(world, "/hooks/payments", "msg_20", Padded(800), unsized: true);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_resolver_reading_the_verified_body_leaves_it_for_the_handler()
    {
        await using var world = await StartAsync();
        var body = $"{{\"tenant\":\"{TenantText}\",\"paid\":true}}";

        using var response = await SendAsync(world, "/hooks/orders", "msg_9", body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (seen, caller) = _probe.Seen.ShouldHaveSingleItem();
        seen.ShouldBe(body, "the resolver read the bytes; the handler still reads the whole body");
        caller.ActorKind.ShouldBe(AlvoActorKind.System);
        caller.Tenant.ShouldBe(_tenant, "the tenant came from the signed body");
    }

    [Fact]
    public async Task A_receiver_with_no_tenant_and_no_resolver_runs_the_handler_as_the_system_in_none()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/plain", "msg_10", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, caller) = _probe.Seen.ShouldHaveSingleItem();
        caller.ActorKind.ShouldBe(AlvoActorKind.System);
        caller.Tenant.ShouldBeNull("System(null) — which every tenant-scoped entity refuses");
    }

    [Fact]
    public async Task A_delivery_whose_id_is_still_being_handled_is_409_and_the_running_one_still_records_it()
    {
        _probe.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var world = await StartAsync();

        var running = SendAsync(world, "/hooks/payments", "msg_12", "{}");
        await _probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var concurrent = await SendAsync(world, "/hooks/payments", "msg_12", "{}");
        _probe.Hold.SetResult();
        using var finished = await running;
        using var retried = await SendAsync(world, "/hooks/payments", "msg_12", "{}");

        concurrent.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await concurrent.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        finished.StatusCode.ShouldBe(HttpStatusCode.OK);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK, "once the running delivery is recorded, a retry is acknowledged");
        _probe.Seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Receivers_are_not_in_the_openapi_document()
    {
        await using var world = await StartAsync(documented: true);

        var paths = (await world.OpenApiDocumentAsync())["paths"]!.AsObject();

        paths.Select(path => path.Key).ShouldNotContain(path => path.StartsWith("/hooks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pattern_mapped_twice_is_refused_because_its_ids_would_be_deduplicated_together() =>
        (await Should.ThrowAsync<InvalidOperationException>(() => StartAsync(alsoMap: app =>
            app.MapAlvoWebhookReceiver("/hooks/payments", "pay-provider", Handle))))
            .Message.ShouldContain("'/hooks/payments'");

    /// <summary>
    /// A receiver's dedup scope is its whole route pattern, group prefix included: one receiver per tenant, each under its
    /// own group, is two receivers whose ids never collide.
    /// </summary>
    [Fact]
    public async Task Receivers_at_one_pattern_under_two_groups_are_two_receivers_each_with_its_own_ids()
    {
        await using var world = await StartAsync(alsoMap: app =>
        {
            app.MapGroup("/tenant-a").MapAlvoWebhookReceiver("/hooks", "pay-provider", Handle, options => options.Tenant = _tenant);
            app.MapGroup("/tenant-b").MapAlvoWebhookReceiver("/hooks", "pay-provider", Handle, options => options.Tenant = _tenant);
        });

        using var first = await SendAsync(world, "/tenant-a/hooks", "msg_24", "{}");
        using var second = await SendAsync(world, "/tenant-b/hooks", "msg_24", "{}");
        using var repeat = await SendAsync(world, "/tenant-a/hooks", "msg_24", "{}");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(2, "each group's receiver runs the id once, and only once");
    }

    [Fact]
    public async Task One_pattern_mapped_twice_under_one_prefix_is_refused_naming_the_whole_pattern()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var world = await StartAsync(alsoMap: app =>
            {
                app.MapGroup("/tenant-a").MapAlvoWebhookReceiver("/hooks", "pay-provider", Handle);
                app.MapGroup("/tenant-a").MapAlvoWebhookReceiver("/hooks", "pay-provider", Handle);
            });
            _ = world.Endpoints;
        });

        refusal.Message.ShouldContain("'/tenant-a/hooks'");
    }

    [Fact]
    public async Task Rows_older_than_seven_days_are_swept()
    {
        await using var world = await StartAsync();
        using var first = await SendAsync(world, "/hooks/payments", "msg_8", "{}");
        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromHours(1));

        _ = await world.Services.GetServices<IHostedService>().OfType<LedgerSweeper>().Single()
            .SweepOnceAsync(TestContext.Current.CancellationToken);
        using var redelivered = await SendAsync(world, "/hooks/payments", "msg_8", "{}");

        redelivered.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(2, "a swept id is forgotten — which is why the window is far shorter than the retention");
    }

    private Task<AlvoApiWorld> StartAsync(
        string? secret = Secret,
        bool documented = false,
        Action<WebApplication>? alsoMap = null,
        bool answeredByAlvo = true,
        IInboundWebhookLedger? ledger = null) =>
        AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [], new AlvoApiWorldSetup(
            MapAlvoProblemDetails: answeredByAlvo,
            MapOpenApiDocument: documented,
            ConfigureServicesAfterAlvo: services =>
            {
                if (ledger is not null)
                {
                    services.AddSingleton(ledger);
                }
            },
            ConfigureServices: services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                services.AddSingleton(_probe);
                services.AddSingleton<ILoggerProvider>(_logs);
                services.Configure<AlvoSecretOptions>(options =>
                {
                    if (secret is not null)
                    {
                        options.Values["pay-provider"] = secret;
                    }
                });
            },
            ConfigureApp: app =>
            {
                app.Use(_probe.ServerLimit is null ? (http, next) => next(http) : _probe.ServerLimit.Enforce);
                app.MapAlvoWebhookReceiver("/hooks/payments", "pay-provider", Handle, options =>
                {
                    options.ResolveTenant = (_, _, _) => ValueTask.FromResult<TenantId?>(_probe.Resolve(_tenant));
                    options.MaxBodyBytes = 1_024;
                });
                app.MapAlvoWebhookReceiver("/hooks/refunds", "pay-provider", Handle, options => options.Tenant = _tenant);
                app.MapAlvoWebhookReceiver("/hooks/orders", "pay-provider", Handle, options => options.ResolveTenant = TenantInBody);
                app.MapAlvoWebhookReceiver("/hooks/plain", "pay-provider", Handle);
                alsoMap?.Invoke(app);
            }));

    /// <summary>The documented way (Ruling 37): the tenant from the signed body, read from the bytes the receiver passes.</summary>
    private static ValueTask<TenantId?> TenantInBody(HttpRequest request, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(body);
        return ValueTask.FromResult<TenantId?>(json.RootElement.TryGetProperty("tenant", out var tenant) ? new TenantId(tenant.GetGuid()) : null);
    }

    private string Now() => _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>A JSON body of exactly <paramref name="bytes"/> UTF-8 bytes.</summary>
    private static string Padded(int bytes) => "{\"pad\":\"" + new string('x', bytes - 10) + "\"}";

    private static async Task<IResult> Handle(HttpRequest request, AlvoCaller caller, Probe probe)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        probe.Ran();
        if (probe.Hold is { } hold)
        {
            probe.Entered.TrySetResult();
            await hold.Task;
        }

        if (probe.FailNext)
        {
            probe.FailNext = false;
            throw new InvalidOperationException("the handler failed");
        }

        if (probe.StatusNext is { } status)
        {
            probe.StatusNext = null;
            return Results.StatusCode(status);
        }

        probe.Seen.Enqueue((body, caller.Context));
        return Results.Ok();
    }

    private Task<HttpResponseMessage> SendAsync(
        AlvoApiWorld world,
        string path,
        string id,
        string body,
        DateTimeOffset? at = null,
        string? signature = null,
        bool unsized = false,
        string? timestampText = null)
    {
        var timestamp = timestampText ?? (at ?? _time.GetUtcNow()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetBytes(body);
        var signed = signature ?? StandardWebhookSignature.Sign(StandardWebhookSignature.Keys(Secret)[0], id, timestamp, bytes);

        HttpContent content = unsized ? new UnsizedContent(bytes) : AlvoApiWorld.RawJson(body);
        return world.SendRawAsync(HttpMethod.Post, path, content: content, headers:
        [
            new(StandardWebhookSignature.IdHeader, id),
            new(StandardWebhookSignature.TimestampHeader, timestamp),
            new(StandardWebhookSignature.SignatureHeader, signed),
        ]);
    }

    private sealed class Probe
    {
        private int _resolved;
        private int _runs;

        internal ConcurrentQueue<(string Body, AlvoContext Caller)> Seen { get; } = new();

        internal bool FailNext { get; set; }

        internal int? StatusNext { get; set; }

        internal ServerBodyLimit? ServerLimit { get; set; }

        internal int Runs => Volatile.Read(ref _runs);

        internal TaskCompletionSource? Hold { get; set; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int TenantsResolved => Volatile.Read(ref _resolved);

        internal void Ran() => Interlocked.Increment(ref _runs);

        internal TenantId Resolve(TenantId tenant)
        {
            Interlocked.Increment(ref _resolved);
            return tenant;
        }
    }

    /// <summary>A body sent without a <c>Content-Length</c>, as a chunked upload is.</summary>
    private sealed class UnsizedContent : HttpContent
    {
        private readonly byte[] _bytes;

        internal UnsizedContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// The server's body limit as Kestrel exposes and enforces it — <c>TestServer</c> does neither: the feature, and a
    /// read past it throwing <see cref="BadHttpRequestException"/> 413.
    /// </summary>
    private sealed class ServerBodyLimit(long limit, bool readOnly = false) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => readOnly;

        public long? MaxRequestBodySize { get; set; } = limit;

        internal Task Enforce(HttpContext http, RequestDelegate next)
        {
            http.Features.Set<IHttpMaxRequestBodySizeFeature>(this);
            http.Request.Body = new LimitedBody(http.Request.Body, this);
            return next(http);
        }
    }

    private sealed class LimitedBody(Stream inner, IHttpMaxRequestBodySizeFeature limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _read += read;
            return _read > limit.MaxRequestBodySize
                ? throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge)
                : read;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A ledger that grants every claim and fails every release — the database gone between the claim and the answer.</summary>
    private sealed class ReleaseFailingLedger : IInboundWebhookLedger
    {
        internal const string Failure = "the ledger is down";

        public Task<WebhookClaimResult> ClaimAsync(string receiver, string webhookId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(new WebhookClaimResult(WebhookClaim.Claimed, "token"));

        public Task<bool> CompleteAsync(string receiver, string webhookId, string token, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ReleaseAsync(string receiver, string webhookId, string token, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Failure);

        public Task<int> SweepAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    /// <summary>Every log line's level and rendered message, from every category.</summary>
    private sealed class LogCapture : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        internal IReadOnlyList<Exception> Exceptions => [.. _exceptions];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue((logLevel, formatter(state, exception)));
            if (exception is not null)
            {
                _exceptions.Enqueue(exception);
            }
        }

        public void Dispose()
        {
        }

        internal IReadOnlyList<string> Messages(LogLevel level) => [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];
    }
}
