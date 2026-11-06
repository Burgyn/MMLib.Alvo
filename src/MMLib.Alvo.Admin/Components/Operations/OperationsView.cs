using MMLib.Alvo.Management;

using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Operations;

/// <summary>The Operations page's words and tones, kept out of the markup so they can be tested.</summary>
internal static class OperationsView
{
    private const string Separator = " · ";

    private static readonly JsonSerializerOptions _fixtureJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The statuses the delivery list filters by, in the order an operator looks for trouble.</summary>
    public static IReadOnlyList<string> StatusFilters { get; } = ["dead", "retrying", "pending", "running", "succeeded", "cancelled", "superseded"];

    /// <summary>The badge a delivery status is drawn with.</summary>
    public static string BadgeClass(string status) => status switch
    {
        "succeeded" => "a-badge a-badge--ok",
        "dead" => "a-badge a-badge--danger",
        "retrying" => "a-badge a-badge--warn",
        "pending" or "running" => "a-badge a-badge--accent",
        _ => "a-badge",
    };

    /// <summary>The badge a delivery is drawn with: a sleeping one reads as waiting, like a pending one.</summary>
    public static string BadgeClass(ManagementDelivery delivery, DateTimeOffset now) =>
        IsSleeping(delivery, now) ? "a-badge a-badge--accent" : BadgeClass(delivery.Status);

    /// <summary>
    /// What a delivery's chip reads: <c>sleeping until 6 Nov 09:00</c> (UTC) for one whose handler is asleep, else its status.
    /// </summary>
    public static string StatusLabel(ManagementDelivery delivery, DateTimeOffset now) => IsSleeping(delivery, now)
        ? $"sleeping until {delivery.NotBefore.UtcDateTime.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}"
        : delivery.Status;

    /// <summary>
    /// Whether a delivery's handler is asleep: it is pending, its wake time is ahead, and its last attempt was skipped — the
    /// mark a sleep leaves. Only a delivery read with its history can tell.
    /// </summary>
    public static bool IsSleeping(ManagementDelivery delivery, DateTimeOffset now) =>
        delivery is { Status: "pending", History: [.., { Outcome: "skipped" }] } && delivery.NotBefore > now;

    /// <summary>The steps a delivery's handler completed, each as its name and when it finished (UTC).</summary>
    public static IReadOnlyList<string> Steps(ManagementDelivery delivery) =>
        [.. delivery.Steps.Select(step => $"{step.Name}{Separator}{step.CompletedAt.UtcDateTime.ToString("d MMM HH:mm:ss", CultureInfo.InvariantCulture)}")];

    /// <summary>A handler's cap on its running deliveries, such as <c>per tenant · 2</c>, or null for none.</summary>
    public static string? ConcurrencyLine(ManagementExtension handler) => handler.Concurrency?.Split(':') is [var key, var limit]
        ? $"per {key}{Separator}{limit}"
        : null;

    /// <summary>A duration in the largest unit it fills, or a dash for none.</summary>
    public static string Duration(TimeSpan? value) => value switch
    {
        null => "—",
        { TotalSeconds: < 1 } brief => Number(brief.TotalMilliseconds, "ms"),
        { TotalMinutes: < 1 } medium => Number(medium.TotalSeconds, "s"),
        { TotalHours: < 1 } longer => Number(longer.TotalMinutes, "min"),
        { } longest => Number(longest.TotalHours, "h"),
    };

    /// <summary>A handler's runs on this instance over the last 24 hours, as one line.</summary>
    public static string RunsLine(ManagementExtensionStat? stat) => stat is not { Invocations: > 0 } runs
        ? "no runs on this instance in the last 24 hours"
        : string.Create(CultureInfo.InvariantCulture, $"{runs.Invocations} runs{Separator}{runs.Failures} failed{Separator}p50 {Duration(runs.P50)}{Separator}p95 {Duration(runs.P95)} — this instance, last 24 hours");

    /// <summary>One attempt of a delivery's history, as one line.</summary>
    public static string AttemptLine(ManagementDeliveryAttempt attempt) => string.Join(Separator, AttemptParts(attempt));

    /// <summary>The question a replay asks before it runs, with the count its dry run answered.</summary>
    public static string ReplayQuestion(ManagementReplayPlan plan, string subscriber) =>
        string.Create(CultureInfo.InvariantCulture, $"Run {plan.Count} dead {(plan.Count == 1 ? "delivery" : "deliveries")} of {subscriber} again?");

    /// <summary>A fixture as the indented JSON it downloads as.</summary>
    public static string FixtureJson(DeliveryFixture fixture) => JsonSerializer.Serialize(fixture, _fixtureJson);

    /// <summary>Whether a delivery is finished, and so may be run again.</summary>
    public static bool CanRedeliver(ManagementDelivery delivery) => delivery.Status is "dead" or "succeeded" or "cancelled";

    /// <summary>When a job runs: a schedule's cron and zone, or the revision a data migration waits for.</summary>
    public static string When(ManagementExtension job) => job.Kind == AlvoExtensionKind.DataMigration
        ? string.Create(CultureInfo.InvariantCulture, $"after revision {job.AfterRevision}")
        : $"{job.Cron}{Separator}{job.TimeZone}";

    /// <summary>
    /// When a schedule next runs, relative to <paramref name="now"/> and as the wall-clock time in its own zone, such as
    /// <c>in 3 h (02:00 Europe/Bratislava)</c>; a dash for a migration or a schedule whose first start has not recorded it.
    /// </summary>
    public static string NextRun(ManagementExtension job, DateTimeOffset now) => job is { NextRunAt: { } next, TimeZone: { } zone }
        ? $"{Until(next - now)} ({Local(next, zone)} {zone})"
        : "—";

    /// <summary>How long ago a schedule's last occurrence ran, or <c>never</c>.</summary>
    public static string LastRun(ManagementExtension job, DateTimeOffset now) =>
        job.LastRunAt is { } last ? $"{Span(now - last)} ago" : "never";

    /// <summary>Whether a job can be run now: a schedule can, a data migration runs once per revision and never on demand.</summary>
    public static bool CanRunNow(ManagementExtension job) => job.Kind == AlvoExtensionKind.Schedule;

    /// <summary>The timer tile's line: how many are armed and how many are due.</summary>
    public static string TimersLine(ManagementTimerCounts? timers) => timers is { } counts
        ? string.Create(CultureInfo.InvariantCulture, $"{counts.Pending} pending{Separator}{counts.Due} due")
        : "this host keeps no timers";

    private static string Until(TimeSpan left) => left <= TimeSpan.Zero ? "due now" : $"in {Span(left)}";

    private static string Span(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "under a minute",
        { TotalHours: < 1 } => Whole(span.TotalMinutes, "min"),
        { TotalDays: < 2 } => Whole(span.TotalHours, "h"),
        _ => Whole(span.TotalDays, "d"),
    };

    private static string Whole(double amount, string unit) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(amount):0} {unit}");

    private static string Local(DateTimeOffset instant, string zone) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static IEnumerable<string> AttemptParts(ManagementDeliveryAttempt attempt)
    {
        yield return string.Create(CultureInfo.InvariantCulture, $"#{attempt.Attempt}");
        yield return attempt.Outcome;
        if (attempt.ErrorType is { } error)
        {
            yield return error;
        }

        yield return Duration(attempt.Duration);
    }

    private static string Number(double amount, string unit) => string.Create(CultureInfo.InvariantCulture, $"{amount:0.##} {unit}");
}
