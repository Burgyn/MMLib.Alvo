using MMLib.Alvo.Extensions.Internal;

using Kinds = MMLib.Alvo.Events.Internal.DeliveryKind;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// What makes a handler registration a job: its delivery kind, and — for a schedule — its cron, zone and scope, or — for a
/// data migration — the revision it waits for, and whether it spans tenants: runs once over every tenant's rows rather than
/// once per tenant on a scoped schema — the framework's own timer re-plan, which reads below the policy layer; a host
/// migration never does.
/// </summary>
internal sealed record JobSpec(string DeliveryKind, string? Cron, string? TimeZone, AlvoScheduleScope Scope, int? AfterRevision, bool SpansTenants = false)
{
    internal bool IsSchedule => DeliveryKind == Kinds.Schedule;

    internal bool IsMigration => DeliveryKind == Kinds.Migration;
}

/// <summary>The framework event types a job is subscribed to: <c>alvo.schedule.&lt;id&gt;</c> and <c>alvo.migration.&lt;id&gt;</c>, <c>-</c> read as <c>_</c>.</summary>
internal static class ScheduleEvents
{
    internal static string TypeFor(string id) => "alvo.schedule." + ExtensionIds.DerivedSegment(id);

    internal static string MigrationTypeFor(string id) => "alvo.migration." + ExtensionIds.DerivedSegment(id);
}
