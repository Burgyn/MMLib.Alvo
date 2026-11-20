using MMLib.Alvo.Tests.Accounts;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>PostgreSQL's leg of <see cref="TenancyEngineFacts"/>: not one assertion of its own.</summary>
/// <remarks>
/// The leg where five creates at the cap really overlap in the engine — no in-process write gate serialises them, so the
/// creator's row lock is what keeps the count at <c>maxPerAccount</c>.
/// </remarks>
public sealed class PostgresTenancyEngineTests : TenancyEngineFacts, IAsyncLifetime
{
    private readonly PostgresApiEngine _engine = new();

    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => _engine;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
