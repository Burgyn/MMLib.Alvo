using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// One job registration as the catalog describes it: a handler of its framework event, with its cron and zone for a schedule,
/// or the revision it waits for for a data migration.
/// </summary>
internal static class JobDescription
{
    internal static ManagementExtension Of(HandlerRegistration handler, JobSpec job, bool exposeTypeNames) =>
        HandlerDescription.Of(handler, exposeTypeNames) with
        {
            Kind = job.IsMigration ? AlvoExtensionKind.DataMigration : AlvoExtensionKind.Schedule,
            Stage = AlvoExtensionStage.Scheduled,
            Cron = job.Cron,
            TimeZone = job.TimeZone,
            AfterRevision = job.AfterRevision,
        };
}
