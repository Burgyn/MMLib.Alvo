namespace MMLib.Alvo.Rules;

/// <summary>
/// Holds the <see cref="PolicyCatalog"/> currently in effect for <see cref="IPolicyEngine"/> —
/// a small, explicitly-primed seam rather than a build-on-first-use cache. <see cref="Current"/> is
/// <see langword="null"/> until something primes it (a successful descriptor apply — see
/// <c>RuntimeSchemaService</c> and the code-first startup path — or a host managing its own
/// lifecycle calling <see cref="SetCurrent"/> directly), and it is re-primed on every subsequent successful
/// apply, so a tightened or revoked rule takes effect for the very next <see cref="IPolicyEngine.Resolve"/>
/// call rather than only after a process restart. <see cref="IPolicyEngine.Resolve"/> reads
/// <see cref="Current"/> without ever blocking on I/O: a descriptor load happens once, at apply time,
/// on whichever thread is already doing that (and is already asynchronous there), never on a request
/// thread resolving a policy.
/// </summary>
public interface IPolicyCatalogProvider
{
    /// <summary>Gets the most recently primed catalog, or <see langword="null"/> when no descriptor has been applied yet.</summary>
    PolicyCatalog? Current { get; }

    /// <summary>
    /// Replaces the current catalog. Called after every successful descriptor apply/rollback; also
    /// public so a host that manages its own descriptor lifecycle (embedded mode, outside
    /// <c>RuntimeSchemaService</c>/the code-first startup path) can prime policy explicitly.
    /// </summary>
    /// <param name="catalog">The newly built catalog to make current.</param>
    void SetCurrent(PolicyCatalog catalog);
}
