namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The shared facts on the default database: SQLite in memory, private to each host.</summary>
public sealed class SqliteAlvoTestHostTests : AlvoTestHostFacts
{
    protected override Task<Action<IAlvoBuilder>> DatabaseAsync() => Task.FromResult<Action<IAlvoBuilder>>(_ => { });
}
