using MMLib.Alvo.Tests.Accounts;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>PostgreSQL's leg of <see cref="AccountsEngineFacts"/>: not one assertion of its own.</summary>
/// <remarks>
/// The leg where two refreshes of one token really overlap in the engine — no in-process write gate serialises them, so
/// the second waits on the first's row lock and must still answer the same child.
/// </remarks>
public sealed class PostgresAccountsEngineTests : AccountsEngineFacts, IAsyncLifetime
{
    private readonly PostgresApiEngine _engine = new();

    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => _engine;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
