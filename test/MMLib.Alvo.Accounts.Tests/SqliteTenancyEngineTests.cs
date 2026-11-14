using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>SQLite's leg of <see cref="TenancyEngineFacts"/>: not one assertion of its own.</summary>
public sealed class SqliteTenancyEngineTests : TenancyEngineFacts
{
    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => SqliteApiEngine.Instance;
}
