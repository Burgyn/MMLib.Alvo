using PublicApiGenerator;
using System.Reflection;
using System.Text.RegularExpressions;

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
    // MMLib.Alvo.Testing ships xunit [Fact]-decorated contract-test base classes
    // (SchemaMigratorContractTests et al.) as part of its public surface. Xunit's Fact/Theory
    // attributes capture the declaring source file's absolute path via [CallerFilePath], which
    // PublicApiGenerator would otherwise render into the baseline — making it different on every
    // machine/CI checkout location, not just on a real API change. Excluding those two attributes
    // keeps the baseline about the actual public surface (types/members), not test metadata.
    private static readonly ApiGeneratorOptions _options = new()
    {
        UseDenyNamespacePrefixesForExtensionMethods = false,
        ExcludeAttributes = ["Xunit.FactAttribute", "Xunit.TheoryAttribute"],
    };

    [Fact]
    public Task Public_api_has_not_changed()
    {
        var assembly = Assembly.Load("MMLib.Alvo.Testing");
        var publicApi = RemoveVerifyDirectoryMetadata(assembly.GeneratePublicApi(_options));
        return Verify(publicApi).UseFileName("PublicApi.MMLib.Alvo.Testing");
    }

    // This assembly references Verify.XunitV3 directly (for SchemaSqlSnapshotTests), which injects
    // [assembly: AssemblyMetadata("Verify.ProjectDirectory", ...)] / ("Verify.SolutionDirectory", ...)
    // carrying the absolute checkout path — non-reproducible across machines/CI, unlike everything
    // else PublicApiGenerator emits here. Strip just those two entries; every other AssemblyMetadata
    // entry (e.g. RepositoryUrl) is stable and stays.
    private static string RemoveVerifyDirectoryMetadata(string publicApi) => Regex.Replace(
        publicApi,
        "\\[assembly: System\\.Reflection\\.AssemblyMetadata\\(\"Verify\\.(?:Project|Solution)Directory\", .*?\\)\\]\r?\n",
        string.Empty,
        RegexOptions.Singleline);
}
