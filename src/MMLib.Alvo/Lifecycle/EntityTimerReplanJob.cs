using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

using System.Collections.Frozen;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// The framework data migration that arms an entity's existing rows when an apply adds a timer or changes its <c>at</c> or
/// <c>when</c>: every row re-planned, one keyset page at a time, against the timers already armed for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent by key.</b> A row whose timer is armed at its due is left alone, so a second run — or a retried attempt —
/// changes nothing; see <see cref="EntityTimerPlanner"/> for the full rule, including why a lapsed due is not armed.
/// </para>
/// <para>
/// <b>The catalog it plans with is the process's current one</b>, not the revision that started the run: a later apply's
/// timers are what a write would arm from then on, so they are what the re-plan arms too.
/// </para>
/// </remarks>
/// <param name="catalogs">The current policy catalog.</param>
/// <param name="compiler">Compiles the always-true predicate the re-plan's rows are read under.</param>
/// <param name="replanner">The data provider's re-plan, or <see langword="null"/> when the provider keeps no timers.</param>
internal sealed class EntityTimerReplanJob(IPolicyCatalogProvider catalogs, ICelCompiler compiler, IEntityTimerReplanner? replanner = null)
{
    /// <summary>How many rows one page — one transaction — re-plans.</summary>
    internal const int PageSize = 100;

    private const string EveryRow = "true";

    /// <summary>Re-plans every row of <paramref name="entity"/>; a no-op when the entity declares no timer any more.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="cancellationToken">Cancelled when the run's timeout passes or the host stops.</param>
    internal async Task RunAsync(string entity, CancellationToken cancellationToken)
    {
        if (replanner is null || Decision(entity) is not { } decision)
        {
            return;
        }

        var caller = AlvoContext.System(null);
        Guid? after = null;
        do
        {
            after = await replanner.ReplanTimersAsync(entity, decision, caller, after, PageSize, cancellationToken).ConfigureAwait(false);
        }
        while (after is not null);
    }

    /// <summary>
    /// An allowed decision that reads every row — no tenant scope, an always-true predicate, nothing hidden — and carries the
    /// entity's compiled lifecycle; <see langword="null"/> when the entity is gone or declares no timer.
    /// </summary>
    private PolicyDecision? Decision(string entity)
    {
        if (catalogs.Current is not { } catalog || !catalog.TryGetEntity(entity, out var policy) || policy.Lifecycle.Timers.Count == 0
            || catalog.Schema.Entities.FirstOrDefault(schema => schema.Name == entity) is not { } schema)
        {
            return null;
        }

        var everyRow = compiler.Compile(EveryRow, CelProfile.Rule, schema).Expression
            ?? throw new InvalidOperationException("The always-true predicate a timer re-plan reads its rows under did not compile.");

        var unrestricted = PolicyDecision.Allow(everyRow, withCheck: null, tenantScope: null, FrozenSet<string>.Empty, FrozenSet<string>.Empty);
        return unrestricted with { Lifecycle = policy.Lifecycle };
    }
}
