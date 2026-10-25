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
    public static IReadOnlyList<string> StatusFilters { get; } = ["dead", "retrying", "pending", "running", "succeeded", "cancelled"];

    /// <summary>The badge a delivery status is drawn with.</summary>
    public static string BadgeClass(string status) => status switch
    {
        "succeeded" => "a-badge a-badge--ok",
        "dead" => "a-badge a-badge--danger",
        "retrying" => "a-badge a-badge--warn",
        "pending" or "running" => "a-badge a-badge--accent",
        _ => "a-badge",
    };

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
