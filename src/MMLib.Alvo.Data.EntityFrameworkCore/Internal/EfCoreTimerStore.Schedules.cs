using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

internal sealed partial class EfCoreTimerStore
{
    public Task SyncSchedulesAsync(IReadOnlyList<ScheduleDefinition> schedules, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ClaimedSchedule>> ClaimSchedulesAsync(string claimant, TimeSpan lease, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<bool> AdvanceScheduleAsync(ScheduleAdvance advance, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task AppendRunAsync(IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<bool> StartMigrationAsync(
        string id, int revision, IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<string>> StartedMigrationsAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
