using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>
/// A DB-less <see cref="IRuntimeSchemaWriter"/> fake that delegates the version-append (with its
/// optimistic-lock conflict semantics) to an injected <see cref="InMemoryDescriptorVersionStore"/>
/// and ignores the plan's SQL — there is no schema to mutate in memory.
/// </summary>
/// <remarks>
/// The fake proves the port's <em>conflict + append</em> contract; the atomic DDL-plus-append
/// guarantee is a property of the real relational writer and is proven by its integration tests,
/// not here.
/// </remarks>
public sealed class InMemoryRuntimeSchemaWriter(InMemoryDescriptorVersionStore store) : IRuntimeSchemaWriter
{
    private readonly InMemoryDescriptorVersionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc/>
    public Task<DescriptorVersion> ApplyAndAppendAsync(
        string project, MigrationPlan plan, DescriptorVersion candidate,
        int expectedRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // plan.Sql is intentionally ignored: an in-memory fake has no physical schema to apply DDL
        // to. Only the append's optimistic-lock semantics are modeled here.
        return _store.AppendAsync(project, candidate, expectedRevision, ct);
    }
}
