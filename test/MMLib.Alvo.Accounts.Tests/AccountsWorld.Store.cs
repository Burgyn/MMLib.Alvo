using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Accounts;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// The world's store, for this suite only: <see cref="IAccountStore"/> is internal to Abstractions, which grants this suite
/// and not the PostgreSQL suite that links the rest of the world — so the one member naming it lives here, and no grant
/// widens for a fact that never calls it.
/// </summary>
internal sealed partial class AccountsWorld
{
    /// <summary>Gets the account store the host signs people in through.</summary>
    internal IAccountStore Store => Api.Services.GetRequiredService<IAccountStore>();
}
