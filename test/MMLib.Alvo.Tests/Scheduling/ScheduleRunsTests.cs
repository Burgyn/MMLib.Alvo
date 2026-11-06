using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Scheduling;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Scheduling;

/// <summary>
/// A schedule's run starts its own chain — the hop table's <i>schedule run</i> row: a fresh correlation, no causation, depth
/// 0 — whatever flow asked for it, a cron round or an operator's run now inside a deep request flow.
/// </summary>
public sealed class ScheduleRunsTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    private static readonly TenantId _tenantA = new(Guid.NewGuid());
    private static readonly TenantId _tenantB = new(Guid.NewGuid());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_minted_inside_a_deep_flow_starts_its_own_chain_at_depth_zero(bool manual)
    {
        IReadOnlyList<AlvoFrameworkEnvelope> minted;
        using (AlvoFlow.Enter(new AlvoProvenance { CorrelationId = "outer", CausationId = "outer-event", Depth = 5 }))
        {
            minted = await Runs().EventsAsync(Schedule(AlvoScheduleScope.EachTenant), _now, manual, Ct);
        }

        var events = minted.Select(framework => framework.Envelope).ToList();
        events.Count.ShouldBe(2);
        events.ShouldAllBe(run => run.ChainDepth == 0 && run.CausationId == null && run.CorrelationId != "outer");
        events[0].CorrelationId.ShouldNotBe(events[1].CorrelationId);
        events.Select(run => run.TenantId).ShouldBe([_tenantA.ToString(), _tenantB.ToString()]);
        events.ShouldAllBe(run => run.Type == "alvo.schedule.nightly_roll" && run.Subject == "schedules/nightly-roll");
    }

    [Fact]
    public async Task Only_a_run_now_carries_the_manual_flag()
    {
        var cron = (await Runs().EventsAsync(Schedule(AlvoScheduleScope.Global), _now, manual: false, Ct)).ShouldHaveSingleItem().Envelope;
        var manual = (await Runs().EventsAsync(Schedule(AlvoScheduleScope.Global), _now, manual: true, Ct)).ShouldHaveSingleItem().Envelope;

        cron.Data.Record.ShouldNotBeNull().Values.ContainsKey("manual").ShouldBeFalse();
        manual.Data.Record.ShouldNotBeNull()["manual"].ShouldBe(true);
        (cron.TenantId, cron.Data.Record["scheduledFor"]).ShouldBe(((string?)null, "2026-11-07T02:00:00.0000000Z"));
    }

    private static ScheduleRuns Runs() => new(new Tenants(_tenantA, _tenantB), new FakeTimeProvider(_now));

    private static HandlerRegistration Schedule(AlvoScheduleScope scope) =>
        Registry(alvo => alvo.AddSchedule<Nightly>("nightly-roll", "0 2 * * *", configure: options => options.Scope = scope)).Handlers.Single();

    private sealed class Nightly : IAlvoScheduledJob
    {
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Tenants(params TenantId[] tenants) : IAlvoTenantCatalog
    {
        public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantId>>(tenants);
    }
}
