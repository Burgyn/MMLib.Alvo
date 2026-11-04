namespace MMLib.Alvo.Api.Tests.Commands;

/// <summary>SQLite's leg of <see cref="CommandEngineFacts"/>.</summary>
public sealed class SqliteCommandTests : CommandEngineFacts
{
    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => SqliteApiEngine.Instance;
}
