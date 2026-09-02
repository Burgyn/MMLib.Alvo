using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Counts reads of <see cref="IPolicyCatalogProvider.Current"/> and calls to
/// <see cref="ISchemaRegistry.GetSchema"/>, forwarding everything to the real provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>One decorator for all three interfaces, because production registers one instance for all three.</b>
/// <c>Rules/Setup.cs</c> registers <c>ISchemaRegistry</c> and <see cref="IRoleCatalogProvider"/> as
/// factories resolving <see cref="IPolicyCatalogProvider"/>, and <see cref="IPolicyCatalogProvider"/>'s own
/// remarks explain why: the rules that judge a request and the schema that validates it must come from one
/// apply, or "the one path on which an unvalidated payload reaches storage is a mismatch between two
/// independently primed holders". Decorating the one registration keeps that identity intact; decorating
/// <c>ISchemaRegistry</c> separately would create the second holder those remarks warn about, and the
/// counted transformer could end up reading an unprimed one.
/// </para>
/// <para>
/// <b><see cref="GetSchema"/> forwards to the inner provider rather than reading this decorator's own
/// <see cref="Current"/>.</b> <c>PolicyCatalogProvider.GetSchema()</c> is itself implemented as
/// <c>Current?.Schema ?? …</c>, so an override written in terms of the local <see cref="Current"/> would
/// entangle the two counters — every schema read would also register as a catalog read, and neither number
/// would mean what its fact claims.
/// </para>
/// </remarks>
/// <param name="inner">The provider Alvo registered, which stays the single primed holder.</param>
internal sealed class CountingPolicyCatalogProvider(IPolicyCatalogProvider inner) : IPolicyCatalogProvider
{
    private int _currentReads;
    private int _schemaReads;

    /// <summary>How many times <see cref="Current"/> has been read.</summary>
    internal int CurrentReads => Volatile.Read(ref _currentReads);

    /// <summary>How many times <see cref="GetSchema"/> has been called.</summary>
    internal int SchemaReads => Volatile.Read(ref _schemaReads);

    /// <summary>Forgets both counts, so one fact can measure one request rather than one plus its setup.</summary>
    internal void Clear()
    {
        Volatile.Write(ref _currentReads, 0);
        Volatile.Write(ref _schemaReads, 0);
    }

    /// <inheritdoc/>
    public PolicyCatalog? Current
    {
        get
        {
            Interlocked.Increment(ref _currentReads);
            return inner.Current;
        }
    }

    /// <inheritdoc/>
    public RoleCatalog? DeclaredRoles => inner.DeclaredRoles;

    /// <inheritdoc/>
    public SchemaModel GetSchema()
    {
        Interlocked.Increment(ref _schemaReads);
        return inner.GetSchema();
    }

    /// <inheritdoc/>
    public void SetCurrent(string project, PolicyCatalog catalog) => inner.SetCurrent(project, catalog);
}
