using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Re-plans the descriptor's timers over the rows an entity already stores, one keyset page per call: what an apply that adds a
/// timer, or changes its <c>at</c> or <c>when</c>, leaves unarmed until each row's next write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, and below the policy layer.</b> The re-plan is system maintenance of a framework table: it reads every row of
/// the entity whatever its tenant and rules, as a rollup recompute does, and never returns a row to anyone.
/// </para>
/// <para>
/// <b>One page is one transaction</b>, which locks its rows before the timer rows — the write path's lock order — so a write
/// racing the re-plan either waits for it or is waited for, and the timer ends at the due of the row as stored.
/// </para>
/// </remarks>
internal interface IEntityTimerReplanner
{
    /// <summary>Re-plans the timers of the page of <paramref name="entity"/>'s rows after <paramref name="after"/>, ordered by id.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="decision">An allowed decision carrying the compiled lifecycle to plan with; its predicates are not applied.</param>
    /// <param name="caller">The system context the re-plan runs as.</param>
    /// <param name="after">The id the previous page ended at, or <see langword="null"/> for the first page.</param>
    /// <param name="limit">The page's size.</param>
    /// <param name="cancellationToken">A token to cancel the page.</param>
    /// <returns>The id of the page's last row when the page was full, or <see langword="null"/> when no row is left.</returns>
    Task<Guid?> ReplanTimersAsync(
        string entity, PolicyDecision decision, AlvoContext caller, Guid? after, int limit, CancellationToken cancellationToken);
}
