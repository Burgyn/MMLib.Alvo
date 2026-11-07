using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.TestHost;
using Shouldly;
using System.Collections.Concurrent;
using System.Net;
using Xunit;

namespace Sales.Tests;

public sealed class AutomationTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "sales-desk.alvo.json");

    [Fact]
    public async Task A_failing_webhook_retries_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        var crm = new FailingOnce();
        var mail = new Outbox();
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor);
            builder.Services.AddSingleton<IEmailSender>(mail);
        }, clock, crm, ct);
        var order = await alvo.Data.CreateAsync("orders", new Dictionary<string, object?> { ["number"] = "SO-7", ["total"] = 2500m }, AlvoTestHost.Caller("staff"), cancellationToken: ct);

        await alvo.DrainEventsAsync(ct);
        clock.Advance(TimeSpan.FromMinutes(5));
        await alvo.DrainEventsAsync(ct);

        crm.Calls.ShouldBe(2, "the webhook failed once and was retried on its own");
        mail.Sent.ShouldHaveSingleItem().Subject.ShouldBe("Big order SO-7");
        (await alvo.Data.GetAsync("orders", (Guid)order["id"]!, AlvoTestHost.Caller("staff"), cancellationToken: ct))!["flagged"].ShouldBe(true);
    }

    private sealed class FailingOnce : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref _calls) == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
    }

    private sealed class Outbox : IEmailSender
    {
        public ConcurrentQueue<AlvoMailMessage> Sent { get; } = new();

        public Task SendAsync(AlvoMailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
