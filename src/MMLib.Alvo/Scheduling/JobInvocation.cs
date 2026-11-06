using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Runs one job delivery: the job built from the delivery's scope, handed the occurrence its framework event names, the
/// event's tenant, and the attempt.
/// </summary>
internal static class JobInvocation
{
    internal const string ScheduledForKey = "scheduledFor";
    internal const string RevisionKey = "revision";

    internal static async Task InvokeAsync<TJob>(IServiceProvider services, object message, AlvoEventContext context, CancellationToken ct)
        where TJob : class
    {
        var job = services.GetRequiredService<TJob>();
        var payload = (AlvoRecord)message;
        var run = AlvoJobContext.ForRun(
            context.Subscriber, ScheduledFor(payload), TenantOf(context.Event), context.Data, context.Attempt, Revision(payload), context.Steps);
        await RunAsync(job, run, ct).ConfigureAwait(false);
    }

    private static Task RunAsync(object job, AlvoJobContext run, CancellationToken ct) => job switch
    {
        IAlvoScheduledJob scheduled => scheduled.RunAsync(run, ct),
        IAlvoDataMigration migration => migration.RunAsync(run, ct),
        _ => throw new InvalidOperationException($"{job.GetType().Name} is registered as a job but implements no job interface."),
    };

    private static DateTimeOffset ScheduledFor(AlvoRecord payload) => payload[ScheduledForKey] switch
    {
        DateTimeOffset at => at.ToUniversalTime(),
        DateTime at => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetDateTimeOffset().ToUniversalTime(),
        var other => throw new FormatException($"A job's '{ScheduledForKey}' must be a timestamp; it is {other?.GetType().Name ?? "missing"}."),
    };

    private static int? Revision(AlvoRecord payload) => payload[RevisionKey] switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } element => element.GetInt32(),
        var value => Convert.ToInt32(value, CultureInfo.InvariantCulture),
    };

    private static TenantId? TenantOf(AlvoEvent @event) =>
        @event.TenantId is { Length: > 0 } tenant ? TenantId.Parse(tenant, CultureInfo.InvariantCulture) : null;
}
