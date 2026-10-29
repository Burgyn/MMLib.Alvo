using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>One minimal-API delegate per <see cref="IAlvoOperations"/> member, gated exactly as the management routes are.</summary>
/// <remarks>
/// <b>Every body is optional at binding</b>, for the reason <c>MapPolicySimulation</c> gives: a required one
/// would be refused 400 by the framework before the gate ran, telling a caller with no level a mapped route
/// from an unmapped one. A body the route cannot use is answered 422 after the gate.
/// </remarks>
internal static partial class ManagementEndpoints
{
    private const string PausedRequired =
        "Send whether the handler should be paused: {\"paused\": true} pauses it, {\"paused\": false} resumes it; 'reason' is optional.";

    private const string EnabledRequired =
        "Send whether the extension should run: {\"enabled\": false} switches it off, {\"enabled\": true} on; 'reason' is optional.";

    private const string ReplayBodyMembers =
        "It reads 'subscriber' (required), 'status' (dead unless set), 'from', 'to', 'dryRun' and 'reason'; send no others, "
        + "because an ignored member could turn a preview into a replay that runs for real.";

    private const string ReplayBodyRequired =
        "A replay needs a JSON body naming the handler: {\"subscriber\": \"…\"}; 'status' (dead unless set), 'from', 'to', 'dryRun' and 'reason' are optional.";

    private const string ReplayQueryRefused =
        "A replay reads no query parameters. Send 'dryRun' in the JSON body instead — {\"subscriber\": \"…\", \"dryRun\": true} — "
        + "since a flag in the query would be ignored and the replay run for real.";

    /// <summary>The page size a delivery list answers with when the caller names none (Ruling 22).</summary>
    private const int DefaultDeliveryPage = 50;

    /// <summary>Maps the nine operations routes under <c>{prefix}/projects/{project}/</c>.</summary>
    /// <param name="group">The management group, whose description exclusion they inherit.</param>
    private static void MapOperations(RouteGroupBuilder group)
    {
        MapDeliveryList(group);
        MapDeliveryReads(group);
        MapDeliveryChanges(group);
        MapCodeControls(group);
        MapScheduleRun(group);
    }

    /// <summary><c>GET deliveries</c> — <see cref="IAlvoOperations.ListDeliveriesAsync"/>.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapDeliveryList(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/deliveries",
                (string project, string? subscriber, string? status, DateTimeOffset? from, DateTimeOffset? to, Guid? after, int? limit,
                    IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.ListDeliveriesAsync(
                        project,
                        new ManagementDeliveryQuery
                        {
                            Subscriber = subscriber,
                            Status = status,
                            From = from,
                            To = to,
                            After = after,
                            Limit = limit ?? DefaultDeliveryPage,
                        },
                        ct))),
            new ManagementRoute(nameof(IAlvoOperations.ListDeliveriesAsync), ManagementOperation.ListDeliveries));

    /// <summary>The delivery detail, its fixture, and the extension stats.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapDeliveryReads(RouteGroupBuilder group)
    {
        Gate(
            group.MapGet(
                "/projects/{project}/deliveries/{delivery:guid}",
                (string project, Guid delivery, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.GetDeliveryAsync(project, delivery, ct))),
            new ManagementRoute(nameof(IAlvoOperations.GetDeliveryAsync), ManagementOperation.GetDelivery));

        Gate(
            group.MapGet(
                "/projects/{project}/deliveries/{delivery:guid}/fixture",
                (string project, Guid delivery, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.GetFixtureAsync(project, delivery, ct))),
            new ManagementRoute(nameof(IAlvoOperations.GetFixtureAsync), ManagementOperation.GetDeliveryFixture));

        Gate(
            group.MapGet(
                "/projects/{project}/extensions/stats",
                (string project, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.GetExtensionStatsAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoOperations.GetExtensionStatsAsync), ManagementOperation.GetExtensionStats));
    }

    /// <summary>Redeliver one delivery and replay many.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapDeliveryChanges(RouteGroupBuilder group)
    {
        Gate(
            group.MapPost(
                "/projects/{project}/deliveries/{delivery:guid}/redeliver",
                (string project, Guid delivery, ManagementReasonBody? body, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.RedeliverAsync(project, delivery, body?.Reason, ct))),
            new ManagementRoute(nameof(IAlvoOperations.RedeliverAsync), ManagementOperation.RedeliverDelivery));

        Gate(
            group.MapPost(
                "/projects/{project}/deliveries/replay",
                (string project, ManagementReplayBody? body, HttpRequest request, IAlvoOperations operations, CancellationToken ct) =>
                    ReplayAsync(project, body, request, operations, ct)),
            new ManagementRoute(nameof(IAlvoOperations.ReplayAsync), ManagementOperation.ReplayDeliveries));
    }

    /// <summary>Pause a handler, switch an extension off or on.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapCodeControls(RouteGroupBuilder group)
    {
        Gate(
            group.MapPut(
                "/projects/{project}/subscribers/{subscriber}/paused",
                (string project, string subscriber, ManagementPausedBody? body, IAlvoOperations operations, CancellationToken ct) =>
                    body?.Paused is { } paused
                        ? Answer(() => operations.SetPausedAsync(project, subscriber, paused, body.Reason, ct))
                        : Refused(ProblemResultFactory.ManagementValidation(PausedRequired))),
            new ManagementRoute(nameof(IAlvoOperations.SetPausedAsync), ManagementOperation.PauseSubscriber));

        Gate(
            group.MapPut(
                "/projects/{project}/extensions/{extension}/enabled",
                (string project, string extension, ManagementEnabledBody? body, IAlvoOperations operations, CancellationToken ct) =>
                    body?.Enabled is { } enabled
                        ? Answer(() => operations.SetEnabledAsync(project, extension, enabled, body.Reason, ct))
                        : Refused(ProblemResultFactory.ManagementValidation(EnabledRequired))),
            new ManagementRoute(nameof(IAlvoOperations.SetEnabledAsync), ManagementOperation.SetExtensionEnabled));
    }

    /// <summary><c>POST schedules/{schedule}/run</c> — <see cref="IAlvoOperations.RunScheduleAsync"/>.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapScheduleRun(RouteGroupBuilder group) =>
        Gate(
            group.MapPost(
                "/projects/{project}/schedules/{schedule}/run",
                (string project, string schedule, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.RunScheduleAsync(project, schedule, ct))),
            new ManagementRoute(nameof(IAlvoOperations.RunScheduleAsync), ManagementOperation.RunSchedule));

    /// <summary>A replay: no query string, one key header at most, then a body that names the handler and nothing it does not read.</summary>
    /// <remarks>
    /// <b>Every shape this route does not read is refused, never ignored (Ruling 28).</b> Apply and rollback take
    /// <c>?dryRun=true</c> in the query; a caller carrying that convention here, or misspelling the body's flag, would otherwise
    /// commit the replay it asked to preview — and a committed reset cannot be undone.
    /// </remarks>
    /// <param name="project">The project addressed.</param>
    /// <param name="body">The bound body, or <see langword="null"/> when none was sent.</param>
    /// <param name="request">The request the query string and the key header are read from.</param>
    /// <param name="operations">The operations port.</param>
    /// <param name="ct">The request's cancellation.</param>
    private static Task<IResult> ReplayAsync(
        string project, ManagementReplayBody? body, HttpRequest request, IAlvoOperations operations, CancellationToken ct)
    {
        if (request.Query.Count > 0)
        {
            return Refused(ProblemResultFactory.ManagementValidation(ReplayQueryRefused));
        }

        if (!TryReadIdempotencyKey(request, out var key))
        {
            return Refused(ProblemResultFactory.ManagementValidation(RepeatedIdempotencyKey));
        }

        return ReplayBodyRefusal(body) is { } refusal
            ? Refused(ProblemResultFactory.ManagementValidation(refusal))
            : Answer(() => operations.ReplayAsync(project, body!.ToRequest(key), ct));
    }

    /// <summary>Why a replay body cannot be used, or <see langword="null"/> when it can.</summary>
    /// <remarks>
    /// The unknown names are echoed because they are what the caller must correct — the line <see cref="UnknownQueryParameter"/>'s
    /// remarks draw; the answer is a problem document returned to the caller who sent them, past the gate.
    /// </remarks>
    /// <param name="body">The bound body, or <see langword="null"/>.</param>
    private static string? ReplayBodyRefusal(ManagementReplayBody? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Subscriber))
        {
            return ReplayBodyRequired;
        }

        return body.UnknownNames is { Count: > 0 } unknown
            ? $"A replay body does not read {string.Join(", ", unknown.Select(name => $"'{name}'"))}. {ReplayBodyMembers}"
            : null;
    }
}
