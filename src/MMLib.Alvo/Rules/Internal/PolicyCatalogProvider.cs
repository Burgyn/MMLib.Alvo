namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The default <see cref="IPolicyCatalogProvider"/>: a single volatile reference, written once per
/// successful apply and read once per <c>IPolicyEngine.Resolve</c> call, with no blocking wait on
/// either side. The project-identity check in <see cref="SetCurrent"/> is guarded by a lock — it
/// only ever runs at apply time, never on the <see cref="Current"/> read path a request takes.
/// </summary>
internal sealed class PolicyCatalogProvider : IPolicyCatalogProvider
{
    private readonly Lock _gate = new();
    private string? _project;
    private PolicyCatalog? _current;

    /// <inheritdoc/>
    public PolicyCatalog? Current => Volatile.Read(ref _current);

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
        }

        Volatile.Write(ref _current, catalog);
    }
}
