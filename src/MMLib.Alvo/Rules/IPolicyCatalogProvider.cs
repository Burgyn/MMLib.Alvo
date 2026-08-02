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
/// <remarks>
/// <para>
/// <strong>Precondition: one project per provider instance, for the lifetime of the host.</strong>
/// This is a single global slot, and <see cref="IPolicyEngine.Resolve"/> carries no project
/// parameter — F3 ships single-project-per-host by design (see the design brief's binding
/// constraints); a provider that silently accepted a second project's catalog would let that
/// project's rules judge the first project's callers, and their entity names would collide. The
/// first <see cref="SetCurrent"/> call fixes the project this provider instance serves for the rest
/// of the process; a later call naming a different project throws rather than mixing the two in.
/// </para>
/// <para>
/// It also serves as the default <see cref="IRoleCatalogProvider"/> — see that port's remarks for
/// why identity roles arrive through a role-shaped port rather than off the policy catalog itself.
/// One instance is registered as both, so the descriptor's <c>auth.roles</c> reaches authentication
/// and rule validation from one apply, primed at one instant, behind one project-identity guard.
/// </para>
/// <para>
/// It likewise serves as the default <see cref="Schema.ISchemaRegistry"/>. A data port has to validate a
/// caller's filter and sort keys, and a write payload, against the entity's declared fields — and it must
/// be the <em>same</em> schema the rules were compiled against, or the one path on which an unvalidated
/// payload reaches storage is a mismatch between two independently primed holders. One instance
/// registered as both means the rules that judge a request and the schema that validates it always come
/// from one apply. A host with its own schema source registers its own
/// <see cref="Schema.ISchemaRegistry"/> and takes it over, exactly as an external identity source does
/// for <see cref="IRoleCatalogProvider"/>.
/// </para>
/// </remarks>
public interface IPolicyCatalogProvider : IRoleCatalogProvider, MMLib.Alvo.Schema.ISchemaRegistry
{
    /// <summary>Gets the most recently primed catalog, or <see langword="null"/> when no descriptor has been applied yet.</summary>
    PolicyCatalog? Current { get; }

    /// <summary>
    /// Replaces the current catalog for <paramref name="project"/>. Called after every successful
    /// descriptor apply/rollback; also public so a host that manages its own descriptor lifecycle
    /// (embedded mode, outside <c>RuntimeSchemaService</c>/the code-first startup path) can prime
    /// policy explicitly.
    /// </summary>
    /// <param name="project">The project <paramref name="catalog"/> was built for.</param>
    /// <param name="catalog">The newly built catalog to make current.</param>
    /// <exception cref="InvalidOperationException">
    /// This provider was already primed for a different project — see the type remarks; F3 supports
    /// exactly one project per host.
    /// </exception>
    void SetCurrent(string project, PolicyCatalog catalog);
}
