using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>SQLite's leg of <see cref="AccountsEngineFacts"/>: not one assertion of its own.</summary>
public sealed class SqliteAccountsEngineTests : AccountsEngineFacts
{
    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => SqliteApiEngine.Instance;
}
