using MMLib.Alvo.Api.Tests;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>PostgreSQL's leg of <see cref="CommandEngineFacts"/>: not one assertion of its own.</summary>
public sealed class PostgresCommandTests : CommandEngineFacts, IAsyncLifetime
{
    private readonly PostgresApiEngine _engine = new();

    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => _engine;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
