namespace MMLib.Alvo.Samples.FieldService.Scheduling;

/// <summary>Whether a moment, or a whole slot, falls inside a customer's service hours, in a named time zone.</summary>
/// <remarks>
/// The local wall clock is what the customer's hours mean, so a slot is converted into the zone before it is compared —
/// the same UTC instant is inside the hours in November and outside them in July, when daylight saving moves it an hour.
/// </remarks>
internal static class ServiceHours
{
    public const string RefusalCode = "outside-service-hours";

    public const string RefusalMessage = "The customer does not accept visits at that time.";

    private const int MinutesPerHour = 60;
    private const int Midnight = 24;

    /// <summary>
    /// <see langword="true"/> when <paramref name="at"/>'s local hour is at or after <paramref name="opensHour"/> and before
    /// <paramref name="closesHour"/>. An absent bound does not restrict. Used for a job's planned start, which has no end.
    /// </summary>
    public static bool Admit(long? opensHour, long? closesHour, DateTimeOffset at, string timeZone)
    {
        var localHour = Local(at, timeZone).Hour;
        return (opensHour is not { } opens || localHour >= opens)
            && (closesHour is not { } closes || localHour < closes);
    }

    /// <summary>
    /// The field a slot breaks, or <see langword="null"/> when it fits: <c>starts_at</c> when it starts before opening,
    /// <c>ends_at</c> when it ends after closing. A customer with an opening hour but no closing hour closes at midnight, so a
    /// slot never runs into the next day; a customer with neither accepts any slot.
    /// </summary>
    public static string? Violation(long? opensHour, long? closesHour, DateTimeOffset startsAt, DateTimeOffset endsAt, string timeZone)
    {
        if (opensHour is null && closesHour is null)
        {
            return null;
        }

        var start = Local(startsAt, timeZone);
        if (opensHour is { } opens && start.Hour < opens)
        {
            return "starts_at";
        }

        var endMinute = (Local(endsAt, timeZone) - start.Date).TotalMinutes;
        return endMinute > (closesHour ?? Midnight) * MinutesPerHour ? "ends_at" : null;
    }

    private static DateTime Local(DateTimeOffset instant, string timeZone) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime;
}
