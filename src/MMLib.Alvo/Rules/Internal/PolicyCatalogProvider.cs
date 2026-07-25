namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The default <see cref="IPolicyCatalogProvider"/>: a single volatile reference, written once per
/// successful apply and read once per <c>IPolicyEngine.Resolve</c> call, with no locking and no
/// blocking wait on either side.
/// </summary>
internal sealed class PolicyCatalogProvider : IPolicyCatalogProvider
{
    private PolicyCatalog? _current;

    /// <inheritdoc/>
    public PolicyCatalog? Current => Volatile.Read(ref _current);

    /// <inheritdoc/>
    public void SetCurrent(PolicyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Volatile.Write(ref _current, catalog);
    }
}
