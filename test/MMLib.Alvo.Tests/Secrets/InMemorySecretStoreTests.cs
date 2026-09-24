using MMLib.Alvo.Secrets;
using MMLib.Alvo.Testing.Secrets;

namespace MMLib.Alvo.Tests.Secrets;

/// <summary>
/// The reference store's leg of the contract suite — which is also what proves the suite runs at all.
/// </summary>
public sealed class InMemorySecretStoreTests : SecretStoreContractTests
{
    protected override ISecretStore CreateStore() => new InMemorySecretStore();
}
