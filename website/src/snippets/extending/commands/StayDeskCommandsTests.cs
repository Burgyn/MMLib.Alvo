using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Events;
using MMLib.Alvo.Secrets;
using Shouldly;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace StayDesk.Tests;

public sealed class StayDeskCommandsTests : IAsyncLifetime
{
    private const string KeySecret = "s3cret-value-long-enough-for-the-floor";
    private const string SigningSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private static readonly Guid _guest = Guid.NewGuid();
    private static readonly Guid _staff = Guid.NewGuid();

    private readonly SqliteConnection _database = new($"Data Source=stay-desk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private readonly Payments _payments = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "stay-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.OpenAsync(Ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IPaymentProvider>(_payments).AddSingleton<IGuestMessenger>(_payments);
        builder.Services.Configure<AlvoEventOptions>(options => options.DispatchMode = AlvoEventDispatchMode.Manual);
        builder.Services.Configure<AlvoSecretOptions>(options => options.Values["payment-provider"] = SigningSecret);
        builder.Services.Configure<AlvoAuthOptions>(options =>
        {
            options.DevKeys.Add(Key("guest", _guest, "guest"));
            options.DevKeys.Add(Key("staff", _staff, "staff"));
        });
        builder.Services.AddAlvo(alvo => alvo.UseSqlite(_database.ConnectionString).FromDescriptor(Descriptor).AddStayDesk());

        _app = builder.Build();
        _app.MapAlvoDataApi();
        _app.MapStayDesk();
        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task Staff_confirm_a_requested_stay_and_its_payment_commits_with_it()
    {
        var stay = await BookAsync();

        using var confirmed = await SendAsync($"/commands/stays/{stay}/confirm", "staff");

        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync($"/api/stays/{stay}", "staff"))["status"]!.GetValue<string>().ShouldBe("confirmed");
        (await ReadAsync("/api/payments", "staff"))["items"]!.AsArray().Count.ShouldBe(1);
        _payments.Charges.ShouldBe(1);
    }

    [Fact]
    public async Task A_guest_is_refused_before_anything_is_charged()
    {
        var stay = await BookAsync();

        using var refused = await SendAsync($"/commands/stays/{stay}/confirm", "guest");

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _payments.Charges.ShouldBe(0);
    }

    [Fact]
    public async Task A_retried_confirmation_is_answered_without_charging_twice()
    {
        var stay = await BookAsync();

        using var first = await SendAsync($"/commands/stays/{stay}/confirm", "staff", idempotency: "confirm-42");
        using var retried = await SendAsync($"/commands/stays/{stay}/confirm", "staff", idempotency: "confirm-42");

        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retried.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        _payments.Charges.ShouldBe(1);
    }

    [Fact]
    public async Task A_guest_cancels_their_own_stay_through_the_command_though_they_may_not_update_it()
    {
        var stay = await BookAsync();

        using var direct = await SendAsync($"/api/stays/{stay}", "guest", method: HttpMethod.Patch, body: new JsonObject { ["status"] = "cancelled" });
        using var cancelled = await SendAsync($"/commands/stays/{stay}/cancel", "guest");

        direct.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadAsync($"/api/stays/{stay}", "guest"))["status"]!.GetValue<string>().ShouldBe("cancelled");
    }

    [Fact]
    public async Task A_signed_payment_notice_is_recorded_once_however_often_it_is_sent()
    {
        var stay = await BookAsync();
        var notice = $"{{\"stayId\":\"{stay}\",\"reference\":\"ch_1\",\"amount\":160}}";

        using var first = await NotifyAsync("msg_1", notice);
        using var again = await NotifyAsync("msg_1", notice);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync("/api/payments", "staff"))["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_notice_signed_with_another_secret_is_refused()
    {
        using var forged = await NotifyAsync("msg_2", "{}", secret: "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("not-the-provider's-secret-at-all")));

        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_internal_command_is_invoked_in_process_under_its_access_rule()
    {
        var commands = _app.Services.GetRequiredService<IAlvoCommands>();
        var stay = await BookAsync();

        await commands.InvokeAsync("remind_guest", stay, AlvoContext.System(null), Ct);

        _payments.Reminders.ShouldBe([stay]);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task<Guid> BookAsync()
    {
        using var created = await SendAsync("/api/stays", "guest", method: HttpMethod.Post, body: new JsonObject
        {
            ["guest_id"] = _guest,
            ["room"] = "12",
            ["status"] = "requested",
            ["nights"] = 2,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<Guid>();
    }

    private async Task<JsonObject> ReadAsync(string path, string key)
    {
        using var response = await SendAsync(path, key, method: HttpMethod.Get);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string key, HttpMethod? method = null, JsonObject? body = null, string? idempotency = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-Alvo-Api-Key", $"{key}.{KeySecret}");
        if (idempotency is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotency);
        }

        return await _client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> NotifyAsync(string id, string body, string secret = SigningSecret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/payments") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", $"v1,{signature}");
        return await _client.SendAsync(request, Ct);
    }

    private static AlvoDevApiKey Key(string id, Guid user, string role)
    {
        var key = new AlvoDevApiKey { KeyId = id, Secret = KeySecret, User = user };
        key.Roles.Add(role);
        key.Scopes.Add("*:read");
        key.Scopes.Add("*:write");
        return key;
    }

    private sealed class Payments : IPaymentProvider, IGuestMessenger
    {
        private int _charges;

        public int Charges => Volatile.Read(ref _charges);

        public List<Guid> Reminders { get; } = [];

        public Task<string> ChargeAsync(Guid stay, decimal amount, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _charges);
            return Task.FromResult($"ch_{stay:N}");
        }

        public Task RemindAsync(Guid stay, CancellationToken cancellationToken)
        {
            Reminders.Add(stay);
            return Task.CompletedTask;
        }
    }
}
