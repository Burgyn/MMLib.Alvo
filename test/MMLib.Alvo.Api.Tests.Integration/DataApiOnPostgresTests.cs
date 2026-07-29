using MMLib.Alvo.Api.Tests;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the shared, engine-sensitive API suite — the second half of #19's DoD, "tests green on
/// SQLite + Postgres", at the <b>HTTP</b> level rather than only at the port level.
/// </summary>
/// <remarks>
/// Not one assertion of its own, deliberately: every fact lives in <see cref="DataApiEngineTests"/> and this
/// class contributes the engine. A fact written here instead would be a fact SQLite never has to satisfy, and
/// the point of the arrangement is that both engines answer the same questions.
/// </remarks>
public sealed class DataApiOnPostgresTests : DataApiEngineTests, IAsyncLifetime
{
    private readonly PostgresApiEngine _engine = new();

    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => _engine;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
