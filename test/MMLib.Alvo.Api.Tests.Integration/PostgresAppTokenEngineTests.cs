using MMLib.Alvo.Tests.Accounts;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>PostgreSQL's leg of <see cref="AppTokenEngineFacts"/>: not one assertion of its own.</summary>
/// <remarks>
/// The leg where a token's sweeps run against a real row-locking engine: a removal, a demotion and a ban revoke the tokens
/// they kill in their own transaction (S5 plan D10), so a rejoined person's old token stays dead here too.
/// </remarks>
public sealed class PostgresAppTokenEngineTests : AppTokenEngineFacts, IAsyncLifetime
{
    private readonly PostgresApiEngine _engine = new();

    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => _engine;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
