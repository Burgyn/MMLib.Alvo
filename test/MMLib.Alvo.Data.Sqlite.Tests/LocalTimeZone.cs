namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs a block as if the host were in another time zone, so a test can prove a bound value is
/// host-independent instead of trusting the machine it happens to run on. CI runs UTC, which is exactly why a
/// zone-dependent parse survives it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TimeZoneInfo.Local"/> has no setter; the supported lever is the <c>TZ</c> environment variable
/// plus <see cref="TimeZoneInfo.ClearCachedData"/>, which discards the cached local zone so the next read
/// resolves it again. <c>TZ</c> is honoured on Unix; on Windows the local zone comes from the registry, so a
/// test using this still <em>passes</em> there (the assertion is that the result is UTC whatever the host
/// says) but no longer <em>bites</em>. The two zones used are deliberately extreme (UTC+14 and UTC−11) so an
/// off-by-one-day error cannot hide inside a single working day.
/// </para>
/// <para>
/// This mutates process-global state, so a test using it must not assert on ambient local time in parallel.
/// Nothing else in this assembly reads the local zone.
/// </para>
/// </remarks>
internal sealed class LocalTimeZone : IDisposable
{
    private const string Variable = "TZ";

    private readonly string? _previous;

    internal LocalTimeZone(string id)
    {
        _previous = Environment.GetEnvironmentVariable(Variable);
        Apply(id);
    }

    public void Dispose() => Apply(_previous);

    private static void Apply(string? id)
    {
        Environment.SetEnvironmentVariable(Variable, id);
        TimeZoneInfo.ClearCachedData();
    }
}
