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
