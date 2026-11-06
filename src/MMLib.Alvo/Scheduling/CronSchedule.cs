using Cronos;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// A schedule's five-field cron in its IANA time zone, evaluated by Cronos: a time in a spring-forward gap runs at the end of
/// the gap, a fixed time in a fall-back overlap runs once, and an hourly or wildcard expression runs in both hours.
/// </summary>
internal sealed class CronSchedule
{
    private readonly CronExpression _expression;
    private readonly TimeZoneInfo _zone;

    private CronSchedule(CronExpression expression, TimeZoneInfo zone)
    {
        _expression = expression;
        _zone = zone;
    }

    /// <summary>Parses <paramref name="cron"/> in <paramref name="timeZone"/>; every way it cannot run is a problem, never an exception.</summary>
    /// <param name="cron">Five fields: minute, hour, day of month, month, day of week.</param>
    /// <param name="timeZone">An IANA zone id, such as <c>Europe/Bratislava</c>.</param>
    /// <param name="now">The instant the expression must still fire after.</param>
    /// <param name="schedule">The parsed schedule, or <see langword="null"/>.</param>
    /// <param name="problem">Why it cannot run, or <see langword="null"/>.</param>
    internal static bool TryParse(string cron, string timeZone, DateTimeOffset now, out CronSchedule? schedule, out string? problem)
    {
        schedule = null;
        TimeZoneInfo? zone = null;
        problem = ExpressionOf(cron, out var expression) ?? ZoneOf(timeZone, out zone);
        if (problem is not null)
        {
            return false;
        }

        var parsed = new CronSchedule(expression!, zone!);
        if (parsed.Next(now) is null)
        {
            problem = NeverFires(cron);
            return false;
        }

        schedule = parsed;
        return true;
    }

    /// <summary>What is wrong with <paramref name="cron"/> alone, in any zone, or <see langword="null"/>.</summary>
    internal static string? CronProblem(string cron, DateTimeOffset now) =>
        ExpressionOf(cron, out var expression)
        ?? (expression!.GetNextOccurrence(now, TimeZoneInfo.Utc) is null ? NeverFires(cron) : null);

    /// <summary>What is wrong with <paramref name="timeZone"/>, or <see langword="null"/>.</summary>
    internal static string? ZoneProblem(string timeZone) => ZoneOf(timeZone, out _);

    /// <summary>The first occurrence strictly after <paramref name="after"/>, in UTC; <see langword="null"/> when there is none.</summary>
    internal DateTimeOffset? Next(DateTimeOffset after) => _expression.GetNextOccurrence(after, _zone)?.ToUniversalTime();

    private static string? ExpressionOf(string cron, out CronExpression? expression)
    {
        if (CronExpression.TryParse(cron, CronFormat.Standard, out expression))
        {
            return null;
        }

        return $"'{cron}' is not a five-field cron (minute hour day-of-month month day-of-week); a seconds field is not supported.";
    }

    private static string NeverFires(string cron) => $"The cron '{cron}' never fires: no date matches it.";

    private static string? ZoneOf(string timeZone, out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return null;
        }
        catch (Exception failure) when (failure is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null;
            return $"'{timeZone}' is not a time zone this machine knows; use an IANA id such as 'Europe/Bratislava' or 'UTC'.";
        }
    }
}
