using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Scheduling;

/// <summary>What the schedule pump logs — ids, occurrences and type names; never a payload or an exception message.</summary>
internal static partial class SchedulePumpLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo could not run schedule {Id} for {ScheduledFor} ({ErrorType}); the occurrence is claimed again when its "
            + "lease ends.")]
    internal static partial void ScheduleRunFailed(ILogger logger, string id, DateTimeOffset scheduledFor, string errorType);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo left schedule {Id} out of the sync: its cron never fires. It does not run.")]
    internal static partial void ScheduleNeverFires(ILogger logger, string id);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo could not sync the registered schedules at start ({ErrorType}); the first schedule round tries again.")]
    internal static partial void ScheduleSyncFailed(ILogger logger, string errorType);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo's schedule pump could not finish a round ({ErrorType}). It keeps running and tries again in {RetryIn}; an "
            + "occurrence it claimed returns when its lease ends.")]
    internal static partial void SchedulePumpRoundFailed(ILogger logger, string errorType, TimeSpan retryIn);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo's schedule pump stopped ({ErrorType}) and will not restart in this process; no schedule runs until it is "
            + "restarted.")]
    internal static partial void SchedulePumpStopped(ILogger logger, string errorType);
}
