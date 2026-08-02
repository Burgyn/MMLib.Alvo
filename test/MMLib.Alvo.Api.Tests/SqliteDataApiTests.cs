namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// SQLite's leg of the shared, engine-sensitive API suite — the half of #19's "green on SQLite + Postgres"
/// that costs no container and therefore runs in ring0.
/// </summary>
/// <remarks>
/// It looks like nothing because that is the point: the facts are declared once in
/// <see cref="DataApiEngineTests"/> and each engine contributes only the engine. The PostgreSQL twin is
/// <c>DataApiOnPostgresTests</c> in <c>MMLib.Alvo.Api.Tests.Integration</c>.
/// </remarks>
public sealed class SqliteDataApiTests : DataApiEngineTests
{
    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => SqliteApiEngine.Instance;
}
