namespace MMLib.Alvo.Samples.FieldService.Scheduling;

/// <summary>Whether a moment falls inside a customer's service hours, in a named time zone.</summary>
internal static class ServiceHours
{
    /// <summary>
    /// <see langword="true"/> when <paramref name="at"/>'s local hour in <paramref name="timeZone"/> is at or after
    /// <paramref name="opensHour"/> and before <paramref name="closesHour"/>. An absent bound does not restrict.
    /// </summary>
    /// <param name="opensHour">The first hour a visit may start, 0–23, or <see langword="null"/>.</param>
    /// <param name="closesHour">The hour by which the customer closes, 1–24, or <see langword="null"/>.</param>
    /// <param name="at">The slot's start.</param>
    /// <param name="timeZone">The IANA zone the hours are read in.</param>
    public static bool Admit(long? opensHour, long? closesHour, DateTimeOffset at, string timeZone)
    {
        var localHour = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).Hour;
        return (opensHour is not { } opens || localHour >= opens)
            && (closesHour is not { } closes || localHour < closes);
    }
}
