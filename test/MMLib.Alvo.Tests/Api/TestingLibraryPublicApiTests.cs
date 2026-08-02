using PublicApiGenerator;

namespace MMLib.Alvo.Tests.Api;

/// <summary>
/// <c>MMLib.Alvo.Testing</c> is not itself packable yet (see its csproj remarks), so it has no
/// <c>MMLib.Alvo.Testing.Tests</c> project of its own and the shared <see cref="PublicApiApprovalTests"/>
/// (which is linked into every test project and targets that project's own sibling assembly) never
/// runs against it. It ships real public surface consumed by every other test project, though
/// (<c>TestFieldSqlRenderer</c> as of this task), so this dedicated approval test loads it directly
/// rather than standing up a whole new project ahead of it earning one.
/// </summary>
public class TestingLibraryPublicApiTests
{
    /// <summary>
    /// <c>MMLib.Alvo.Testing</c> ships xunit <c>[Fact]</c>-decorated contract-test base classes
    /// (<c>SchemaMigratorContractTests</c> et al.) as part of its public surface. Xunit's
    /// Fact/Theory attributes capture the declaring source file's absolute path via
    /// <c>[CallerFilePath]</c>, which <c>PublicApiGenerator</c> would otherwise render into the
    /// baseline — making it different on every machine/CI checkout location, not just on a real API
    /// change. Excluding those two attributes keeps the baseline about the actual public surface
    /// (types/members), not test metadata.
    /// </summary>
    private static readonly ApiGeneratorOptions _options = new()
    {
        UseDenyNamespacePrefixesForExtensionMethods = false,
        ExcludeAttributes = ["Xunit.FactAttribute", "Xunit.TheoryAttribute"],
    };

    [Fact]
    public Task Public_api_has_not_changed()
    {
        var assembly = typeof(TestFieldSqlRenderer).Assembly;
        var publicApi = VerifyBuildMetadata.RemoveFrom(assembly.GeneratePublicApi(_options));
        return Verify(publicApi).UseFileName("PublicApi.MMLib.Alvo.Testing");
    }
}
