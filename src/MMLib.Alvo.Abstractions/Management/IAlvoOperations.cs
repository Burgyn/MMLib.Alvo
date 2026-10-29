namespace MMLib.Alvo.Management;

/// <summary>
/// <b>The operator's surface over host code that runs after a commit</b>: read handler deliveries, run a dead one again,
/// replay a window, pause a handler, take a delivery away as a redacted fixture, and see how each extension ran. The admin
/// dashboard calls it in-process; an agent and the CLI reach the same members over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member gates itself, as <see cref="IAlvoManagement"/>'s do</b>, against the same level table: reads need
/// <c>viewer</c>, every change and the fixture need <c>developer</c>, switching an extension off needs <c>admin</c>. An
/// in-process caller that publishes no principal is refused.
/// </para>
/// <para>
/// <b>Every change is audited and idempotent.</b> A pause, resume, redeliver or replay that changed something writes one
/// row to the operations log and one <c>ManagementAudit</c> log event; one that changed nothing — a second pause, a
/// redeliver of a delivery already pending — writes neither and answers the current state. A real replay may carry an
/// idempotency key: a retry under it answers the first replay's count again, with <see cref="ManagementReplayPlan.Replayed"/>.
/// </para>
/// <para>
/// <b>A redelivered or replayed delivery runs its handler again with a fresh attempt budget</b>, its history kept; its
/// condition is not evaluated again — it was decided when the event was fanned out.
/// </para>
/// </remarks>
public interface IAlvoOperations
{
    /// <summary>A page of deliveries, newest first, without payloads.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="query">Which deliveries, and the page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The page and the next page's cursor.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>viewer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRequestException">An unknown status, a page size outside 1–200, or a window that ends before it starts.</exception>
    Task<ManagementDeliveryPage> ListDeliveriesAsync(string project, ManagementDeliveryQuery query, CancellationToken ct = default);

    /// <summary>One delivery and its attempts.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="delivery">The delivery id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The delivery with <see cref="ManagementDelivery.History"/> filled.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>viewer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementDeliveryNotFoundException">No such delivery was recorded.</exception>
    Task<ManagementDelivery> GetDeliveryAsync(string project, Guid delivery, CancellationToken ct = default);

    /// <summary>
    /// Runs a finished delivery — dead, succeeded or cancelled — again: back to pending, due now, with a fresh attempt
    /// budget. A dead delivery that held its record's later deliveries back stops holding them once it succeeds.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="delivery">The delivery id.</param>
    /// <param name="reason">Why, kept in the audit; at most 500 characters, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The delivery as it is now; one already pending or retrying is returned unchanged.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementDeliveryNotFoundException">No such delivery was recorded.</exception>
    /// <exception cref="ManagementConflictException">
    /// It is running; its record has another delivery of the handler running; it is dead with <c>chain-depth-exceeded</c> (a loop the
    /// chain-depth limit stopped); or its handler is not registered in this host.
    /// </exception>
    /// <exception cref="ManagementRequestException">The reason is too long or holds a control character.</exception>
    Task<ManagementDelivery> RedeliverAsync(string project, Guid delivery, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Counts (a dry run) or runs again (a real run) one handler's finished deliveries of one status within a window of event
    /// time — never one dead with <c>chain-depth-exceeded</c>. A real run resets at most 10 000 deliveries.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="request">The handler, the status, the window, whether to only count, the reason and the key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count and the first 20 ids.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such handler.</exception>
    /// <exception cref="ManagementRequestException">
    /// A status that is not finished, a window that ends before it starts, more than 10 000 deliveries, a key on a dry run or
    /// one the caller cannot hold, or a bad reason.
    /// </exception>
    /// <exception cref="ManagementConflictException">A real run selects a delivery whose record has a delivery of the handler running.</exception>
    /// <exception cref="Data.AlvoIdempotencyConflictException">The key was spent by this caller on a different replay.</exception>
    Task<ManagementReplayPlan> ReplayAsync(string project, ManagementReplayRequest request, CancellationToken ct = default);

    /// <summary>A delivery as a fixture: its envelope with every non-key value redacted, to reproduce elsewhere.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="delivery">The delivery id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fixture.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementDeliveryNotFoundException">No such delivery was recorded.</exception>
    /// <exception cref="ManagementConflictException">The outbox no longer holds its event.</exception>
    Task<DeliveryFixture> GetFixtureAsync(string project, Guid delivery, CancellationToken ct = default);

    /// <summary>
    /// Pauses or resumes a handler on every instance: while paused its deliveries are recorded and wait; once resumed they
    /// run in order.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="subscriber">The handler id.</param>
    /// <param name="paused">Whether it should be paused.</param>
    /// <param name="reason">Why, kept on the pause and in the audit; at most 500 characters, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The handler's catalog entry as it is now.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such handler.</exception>
    /// <exception cref="ManagementRequestException">The reason is too long or holds a control character.</exception>
    Task<ManagementExtension> SetPausedAsync(string project, string subscriber, bool paused, string? reason, CancellationToken ct = default);

    /// <summary>Switches an extension that may be switched off — a validate hook — off or on.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="extension">The extension id.</param>
    /// <param name="enabled">Whether it should run.</param>
    /// <param name="reason">Why, kept in the audit, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The extension's catalog entry as it is now.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>admin</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such extension.</exception>
    /// <exception cref="ManagementConflictException">Its kind cannot be switched off — an event handler is paused instead.</exception>
    Task<ManagementExtension> SetEnabledAsync(string project, string extension, bool enabled, string? reason, CancellationToken ct = default);

    /// <summary>Runs a schedule now, as a delivery of its own.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="schedule">The schedule id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The delivery that will run it.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such schedule.</exception>
    Task<ManagementDelivery> RunScheduleAsync(string project, string schedule, CancellationToken ct = default);

    /// <summary>How each extension ran on this instance over the last day.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The numbers, scoped to <c>this-instance</c>.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>viewer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<ManagementExtensionStats> GetExtensionStatsAsync(string project, CancellationToken ct = default);
}
