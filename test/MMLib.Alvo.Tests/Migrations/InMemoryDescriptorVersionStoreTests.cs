using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class InMemoryDescriptorVersionStoreTests : DescriptorVersionStoreContractTests
{
    protected override IDescriptorVersionStore CreateStore() => new InMemoryDescriptorVersionStore();
}
