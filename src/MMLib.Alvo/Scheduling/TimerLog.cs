using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Scheduling;

/// <summary>What the timer pump logs — scopes, keys and type names; never a payload or an exception message.</summary>
internal static partial class TimerLog
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo cancelled timer {Key} of scope '{Scope}': its stored row cannot be built into an event ({ErrorType}) — "
            + "a malformed payload or provenance, a scope that is not a tenant id, or a type the custom-event door refuses. It is not retried.")]
    internal static partial void TimerUnbuildable(ILogger logger, string scope, string key, string errorType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo could not fire timer {Key} of scope '{Scope}' ({ErrorType}); it stays armed and is claimed again when "
            + "its lease ends.")]
    internal static partial void TimerFireFailed(ILogger logger, string scope, string key, string errorType);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo's timer pump stood down: no data provider registered a timer store, so no timer can be armed or fired.")]
    internal static partial void TimerStoreAbsent(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo's timer pump is in the manual dispatch mode (Alvo:Events:DispatchMode = Manual): its background loop "
            + "does not run, and timers fire only in IAlvoEventPump.DrainAsync rounds.")]
    internal static partial void TimerPumpManual(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo's timer pump stood down: the boot finished without a primed policy catalog. No timer was fired; every "
            + "armed one is still armed. It starts on the next boot that primes a schema.")]
    internal static partial void TimerPumpStoodDownUnprimed(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo's timer pump could not finish a round ({ErrorType}). It keeps running and tries again in {RetryIn}; a "
            + "timer it claimed returns when its lease ends.")]
    internal static partial void TimerPumpRoundFailed(ILogger logger, string errorType, TimeSpan retryIn);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo's timer pump stopped ({ErrorType}) and will not restart in this process; no timer fires until it is "
            + "restarted.")]
    internal static partial void TimerPumpStopped(ILogger logger, string errorType);
}
