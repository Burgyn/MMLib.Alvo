using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>
/// Runs one write's <c>before*</c> hooks — <c>reject</c> and <c>mutate</c> — <b>inside the write's own
/// transaction</b>, and answers with the patch the caller must apply to the row it is about to write.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port rather than a call into the core, because every caller is a driver.</b> The compiled hooks live
/// in the core's policy catalog and are evaluated by the core's CEL interpreter, while the only place a hook
/// may legitimately run is inside a storage driver's own transaction — and a driver's data port is
/// <see langword="internal"/> to its package and depends on <c>MMLib.Alvo.Abstractions</c> alone. One port is
/// what makes every driver run the <em>same</em> pipeline instead of each growing its own, which is what §0
/// principle 3 asks of a rule-engine behaviour.
/// </para>
/// <para>
/// <b>The signature is synchronous, and that is the network ban rather than a style choice.</b> The frozen
/// schema states it in the slot's own description — <c>"Before-actions run in-transaction: reject or mutate
/// only. No network, no external calls."</c> — because a hook holds a write transaction open while it runs,
/// so one HTTP call inside one is a row lock held for a stranger's timeout. A method that returns no
/// <see cref="System.Threading.Tasks.Task"/> and takes no <see cref="System.Threading.CancellationToken"/>
/// cannot await anything, so the shortest path to a network call is already closed at the contract: an
/// implementation wanting one would have to block a transaction-holding thread on it. The indirect route —
/// an injected service that itself can reach the network — is closed by an architecture fact over the default
/// implementation's own dependencies, because a signature cannot express "and nothing you hold may do it
/// either". After-hooks are where a network call belongs.
/// </para>
/// <para>
/// <b>What bounds the time a hook may spend, given there is no cancellation token to bound it with.</b> The
/// bound is structural, and it is the reason no timeout is offered. A hook is a fixed number of compiled CEL
/// expressions — the count fixed by the descriptor at apply, never by the request — and the profile they
/// compile in has no loop, no comprehension macro, no recursion, no user-defined function and no I/O: the
/// only two calls it allow-lists are an ASCII fold over one string and a read of an instant the caller
/// already bound. Each expression's tree is walked once, and its node count is bounded by its source length,
/// which the frozen schema caps at 2000 characters. So the work is O(descriptor), not O(caller input): no
/// request can make a hook slower, and a wall-clock budget could only fire on a machine that had already
/// stopped serving. A timeout would add a clock read per hook plus a second failure mode inside a
/// transaction, to guard against an overrun the grammar cannot express.
/// </para>
/// <para>
/// <b>A refusal is an exception and the patch is a return value, deliberately in that asymmetry.</b> A
/// <c>reject</c> that came back as a value would be a refusal a driver can forget to check — and a forgotten
/// refusal is a hook that silently does not guard the write it was declared to guard, which is worse than no
/// hook at all. It is thrown as <see cref="AlvoAuthorizationException"/>, the family
/// <see cref="Data.IAlvoData"/> already reserves for "the operation is not permitted", carrying the author's
/// own <c>reject</c> text; that text and the hook's JSON pointer are both authored into the descriptor by
/// whoever controls the backend, never supplied by the caller, so naming them discloses nothing the
/// descriptor did not already declare (the same argument that exception's remarks make for a field name).
/// </para>
/// </remarks>
public interface IBeforeHookRunner
{
    /// <summary>
    /// Runs every <c>before*</c> hook <paramref name="entity"/> declares for <paramref name="operation"/>,
    /// in declaration order, and returns the fields a <c>mutate</c> changed.
    /// </summary>
    /// <param name="entity">The entity being written.</param>
    /// <param name="operation">
    /// The write being performed, which selects the hook point: <see cref="DataOperation.Create"/>,
    /// <see cref="DataOperation.Update"/> or <see cref="DataOperation.Delete"/>. A read operation selects no
    /// hook and answers with an empty patch — the frozen schema declares no <c>beforeGet</c>/<c>beforeList</c>
    /// point, so it is "no hooks" rather than a throw on an operation this subsystem is not about.
    /// </param>
    /// <param name="candidate">
    /// The row the write would produce — the <b>complete post-image</b>, never the caller's partial payload,
    /// for the same reason <see cref="Expressions.IPredicateEvaluator.Evaluate"/> requires one: a field the
    /// caller did not mention has to read as its stored value. On a delete, where there is no post-image, this
    /// is the row being deleted.
    /// </param>
    /// <param name="previous">
    /// The row as it was before the change — the in-transaction, row-locked pre-image — or
    /// <see langword="null"/> on a create, where there is none.
    /// </param>
    /// <param name="context">The caller the write is performed as; what <c>@user</c>/<c>@tenant</c> resolve against.</param>
    /// <param name="now">
    /// The instant <c>now()</c> resolves to inside a <c>mutate</c>: the one this write is already stamped
    /// with, so a value a hook writes and the row's own <c>updated_at</c> cannot disagree. Bound once by the
    /// caller, never read here.
    /// </param>
    /// <returns>
    /// The fields a <c>mutate</c> set, keyed by field name, and empty when no hook fired or none mutated. A
    /// <see langword="null"/> value is a value — "store nothing here" — and not an absence.
    /// </returns>
    /// <exception cref="AlvoAuthorizationException">A hook's <c>reject</c> fired; the write must not proceed.</exception>
    IReadOnlyDictionary<string, object?> Run(
        string entity,
        DataOperation operation,
        AlvoRecord candidate,
        AlvoRecord? previous,
        AlvoContext context,
        DateTimeOffset now);
}
