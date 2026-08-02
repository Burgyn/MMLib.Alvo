using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Rules;

namespace MMLib.Alvo.Tests.Rules;

public class PolicyEngineContractTestsOverCatalog : PolicyEngineContractTests
{
    protected override IPolicyEngine CreateEngine(AlvoDescriptor descriptor, SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, MMLib.Alvo.Tests.Expressions.CelFixtures.Compiler);
        var provider = new MMLib.Alvo.Rules.Internal.PolicyCatalogProvider();
        provider.SetCurrent(descriptor.Name, catalog);
        return new MMLib.Alvo.Rules.Internal.PolicyEngine(provider);
    }
}
