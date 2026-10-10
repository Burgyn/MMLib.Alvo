using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The default <see cref="IPolicyCatalogProvider"/>: a single volatile reference, written once per
/// successful apply and read once per <c>IPolicyEngine.Resolve</c> call, with no blocking wait on
/// either side. The project-identity check <b>and</b> the publish in <see cref="SetCurrent"/> share
/// one lock, so a catalog only ever becomes current as one atomic step with the guard that admitted
/// it, and two concurrent applies publish in the order the guard admitted them rather than in an
/// arbitrary one. Both only ever run at apply time; the <see cref="Current"/> read path a request
/// takes never takes the lock.
/// </summary>
internal sealed class PolicyCatalogProvider : IPolicyCatalogProvider
{
    private static readonly SchemaModel _unprimedSchema = new([]);

    private readonly Lock _gate = new();
    private string? _project;
    private PolicyCatalog? _current;

    /// <inheritdoc/>
    public PolicyCatalog? Current => Volatile.Read(ref _current);

    /// <inheritdoc/>
    /// <remarks>
    /// One volatile read of the same reference <see cref="Current"/> serves, so the roles a request
    /// authenticates against and the rules that judge it always come from the same applied
    /// descriptor — never from two holders primed a moment apart.
    /// </remarks>
    public RoleCatalog? DeclaredRoles => Current?.Roles;

    /// <inheritdoc/>
    /// <remarks>
    /// The same single volatile read <see cref="Current"/> and <see cref="DeclaredRoles"/> take, so a
    /// request's rules, its role set and the schema validating its field names always come from one
    /// applied descriptor. An unprimed provider reports an <em>empty</em> model rather than
    /// <see langword="null"/> — unlike <see cref="DeclaredRoles"/>, whose port distinguishes "no set
    /// declared" from "an empty set". Empty is the fail-closed value here: no entity declared means every
    /// entity name and every field name a caller supplies is refused, and <c>IPolicyEngine</c> has already
    /// denied the operation one layer earlier anyway.
    /// </remarks>
    public SchemaModel GetSchema() => Current?.Schema ?? _unprimedSchema;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Invariant: every publication carries a <see cref="SchemaModel"/> instance no earlier publication
    /// carried.</b> Readers key "the applied revision did not change" on the instance <see cref="GetSchema"/>
    /// returns: <c>AppliedSchemaView</c> reads it before and after a policy decision and keeps the decision only if
    /// it is the same object, and the EF port's <c>AlvoDataContextFactory</c> mints its model token from it.
    /// Republishing an instance that was current before (A → B → A) would let a decision taken against B pass that
    /// check against A's fields.
    /// </para>
    /// <para>
    /// It is upheld by the callers rather than by copying here, because <c>GetSchema</c> returning the very schema
    /// the catalog was built from is itself pinned (<c>PolicyCatalogProviderSchemaTests</c>). Every caller maps a
    /// fresh model from a descriptor — the runtime apply, the re-apply priming, the boot plan and the code-first
    /// runner — except a rollback, which restores a stored schema and therefore publishes a copy of it
    /// (<c>RuntimeSchemaService.RollbackAsync</c>, pinned by
    /// <c>Rollback_publishes_a_schema_instance_no_earlier_revision_published</c>). A new caller that publishes a
    /// schema it did not map itself owes the same copy.
    /// </para>
    /// </remarks>
    public void SetCurrent(string project, PolicyCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(catalog);

        lock (_gate)
        {
            _project ??= project;
            if (!string.Equals(_project, project, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"This policy catalog provider was already primed for project '{_project}'; it cannot " +
                    $"also be primed for project '{project}'. F3 supports exactly one project per host.");
            }

            Volatile.Write(ref _current, catalog);
        }
    }
}
