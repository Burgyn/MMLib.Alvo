using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>SQLite's leg of <see cref="InvitationEngineFacts"/>: not one assertion of its own.</summary>
public sealed class SqliteInvitationEngineTests : InvitationEngineFacts
{
    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => SqliteApiEngine.Instance;
}
