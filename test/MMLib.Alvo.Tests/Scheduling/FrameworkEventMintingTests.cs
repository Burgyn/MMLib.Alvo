using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

namespace MMLib.Alvo.Tests.Scheduling;

/// <summary>
/// A schedule's run is a framework event, minted only by the framework: the one door is called from exactly the framework's
/// own paths, and neither door a host holds — publishing an event or arming a timer — accepts a schedule's event type.
/// </summary>
public sealed class FrameworkEventMintingTests
{
    private const string ScheduleRun = "alvo.schedule.nightly_roll";
    private const string MigrationRun = "alvo.migration.backfill_notes";
    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A command's completion and a schedule's or data migration's run are the runtime's callers; the timer-store contract suite is the one other,
    /// building the envelopes a provider under test is handed — through the grant the Testing package holds, not a host's.
    /// </summary>
    [Fact]
    public void Only_the_framework_paths_call_AlvoFrameworkEvent_Create() =>
        ShippedSources.FileNamesMentioning("AlvoFrameworkEvent.Create(")
            .ShouldBe(["CommandCompletion.cs", "ScheduleRuns.cs", "TimerStoreContractTests.Schedules.cs"], ignoreOrder: true);

    [Fact]
    public async Task A_host_cannot_publish_or_schedule_a_schedule_run()
    {
        var outbox = Substitute.For<IOutboxStore>();
        var timers = Substitute.For<ITimerStore>();
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(HandlerCatalog.Catalog());
        var events = new AlvoEvents(outbox, new FakeTimeProvider(_now), AlvoChainDepthLimit.Default);
        var scheduler = new AlvoScheduler(timers, catalogs, AlvoChainDepthLimit.Default, new FakeTimeProvider(_now));

        await Should.ThrowAsync<ArgumentException>(() => events.PublishAsync(ScheduleRun, "schedules/nightly-roll", null, AlvoContext.System(null), Ct));
        await Should.ThrowAsync<ArgumentException>(() => scheduler.ScheduleAsync("nightly", _now, ScheduleRun, null, cancellationToken: Ct));
        await Should.ThrowAsync<ArgumentException>(() => events.PublishAsync(MigrationRun, "migrations/backfill-notes", null, AlvoContext.System(null), Ct));
        await Should.ThrowAsync<ArgumentException>(() => scheduler.ScheduleAsync("backfill", _now, MigrationRun, null, cancellationToken: Ct));

        await outbox.DidNotReceiveWithAnyArgs().AppendAsync(default(AlvoCustomEvent)!, Ct);
        await timers.DidNotReceiveWithAnyArgs().UpsertAsync(default!, Ct);
    }
}
