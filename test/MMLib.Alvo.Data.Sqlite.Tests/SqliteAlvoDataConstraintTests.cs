using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the constraint suite — #139's answer for this engine. It supplies a store and nothing else,
/// so a fact cannot be weakened to make the driver pass.
/// </summary>
/// <remarks>
/// The engine that names the violated <em>columns</em> but never the constraint, and that names nothing at all
/// for a foreign key. Both differences are absorbed by <c>SqliteSqlDialect.DecodeConstraintViolation</c>, which
/// is the point of the seam: every fact above it reads identically to PostgreSQL's.
/// </remarks>
public sealed class SqliteAlvoDataConstraintTests : AlvoDataConstraintTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Data;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
