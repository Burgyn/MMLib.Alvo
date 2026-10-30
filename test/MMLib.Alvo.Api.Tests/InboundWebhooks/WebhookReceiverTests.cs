using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Api;
using MMLib.Alvo.InboundWebhooks.Internal;
using MMLib.Alvo.Secrets;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Api.Tests.InboundWebhooks;

/// <summary>
/// Inbound webhooks over HTTP (spec 2026-10-14 §4.9, acceptance (4)): verified, inside the window, once per
/// <c>(pattern, webhook-id)</c>, run as the system in the configured tenant, and forgotten after seven days.
/// </summary>
public sealed class WebhookReceiverTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string TenantText = "7b8f0c4e-2b8e-4f6f-9b1e-0c7a2d1f3e55";
    private static readonly TenantId _tenant = new(Guid.Parse(TenantText));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
    private readonly Probe _probe = new();

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
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(1);
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
        var forged = StandardWebhookSignature.Sign([], "msg_11", _time.GetUtcNow().ToUnixTimeSeconds(), "{}"u8);

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

    private Task<AlvoApiWorld> StartAsync(string? secret = Secret, bool documented = false, Action<WebApplication>? alsoMap = null) =>
        AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [], new AlvoApiWorldSetup(
            MapAlvoProblemDetails: true,
            MapOpenApiDocument: documented,
            ConfigureServices: services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                services.AddSingleton(_probe);
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
                app.MapAlvoWebhookReceiver("/hooks/payments", "pay-provider", Handle, options =>
                {
                    options.ResolveTenant = (_, _) => _probe.Resolve(_tenant);
                    options.MaxBodyBytes = 1_024;
                });
                app.MapAlvoWebhookReceiver("/hooks/refunds", "pay-provider", Handle, options => options.Tenant = _tenant);
                app.MapAlvoWebhookReceiver("/hooks/orders", "pay-provider", Handle, options => options.ResolveTenant = TenantInBody);
                app.MapAlvoWebhookReceiver("/hooks/plain", "pay-provider", Handle);
                alsoMap?.Invoke(app);
            }));

    /// <summary>The documented way (Ruling 37): the tenant from the signed body, read from the bytes the receiver passes.</summary>
    private static TenantId? TenantInBody(HttpRequest request, ReadOnlyMemory<byte> body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tenant", out var tenant) ? new TenantId(tenant.GetGuid()) : null;
    }

    private static async Task<IResult> Handle(HttpRequest request, AlvoCaller caller, Probe probe)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
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

        probe.Seen.Enqueue((body, caller.Context));
        return Results.Ok();
    }

    private Task<HttpResponseMessage> SendAsync(AlvoApiWorld world, string path, string id, string body, DateTimeOffset? at = null, string? signature = null)
    {
        var timestamp = (at ?? _time.GetUtcNow()).ToUnixTimeSeconds();
        var bytes = Encoding.UTF8.GetBytes(body);
        var signed = signature ?? StandardWebhookSignature.Sign(StandardWebhookSignature.Keys(Secret)[0], id, timestamp, bytes);

        return world.SendRawAsync(HttpMethod.Post, path, content: AlvoApiWorld.RawJson(body), headers:
        [
            new(StandardWebhookSignature.IdHeader, id),
            new(StandardWebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture)),
            new(StandardWebhookSignature.SignatureHeader, signed),
        ]);
    }

    private sealed class Probe
    {
        private int _resolved;

        internal ConcurrentQueue<(string Body, AlvoContext Caller)> Seen { get; } = new();

        internal bool FailNext { get; set; }

        internal TaskCompletionSource? Hold { get; set; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int TenantsResolved => Volatile.Read(ref _resolved);

        internal TenantId Resolve(TenantId tenant)
        {
            Interlocked.Increment(ref _resolved);
            return tenant;
        }
    }
}
