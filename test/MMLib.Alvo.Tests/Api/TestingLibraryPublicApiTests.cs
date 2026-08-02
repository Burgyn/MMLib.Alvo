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
        var publicApi = RemoveVerifyBuildMetadata(assembly.GeneratePublicApi(_options));
        return Verify(publicApi).UseFileName("PublicApi.MMLib.Alvo.Testing");
    }

    /// <summary>
    /// This assembly references <c>Verify.XunitV3</c> directly (for <c>SchemaSqlSnapshotTests</c>), and
    /// Verify's build targets stamp an <c>[assembly: AssemblyMetadata("Verify.&lt;key&gt;", ...)]</c> entry
    /// per build-time fact it wants to recover at runtime — directories, the solution name, the target
    /// frameworks. **None of them are Alvo's public API**, and every one of them is derived from how
    /// and where the build ran, so each is a way for this baseline to fail for a reason that has
    /// nothing to do with the public surface: the directories carry an absolute checkout path
    /// (different on every machine and in CI), and the rest vary with the build driver — a Stryker
    /// mutation run builds the solution itself and this test errored there while passing under
    /// <c>dotnet test</c>. Verify 31.27.0 also *added* one (<c>Verify.IntermediateDirectory</c>) to the
    /// two that existed when this filter was first written, so a patch bump alone was enough to break
    /// the gate. The whole family therefore goes. Every non-Verify <c>AssemblyMetadata</c> entry
    /// (<c>RepositoryUrl</c>) is genuine metadata and stays.
    /// </summary>
    private static string RemoveVerifyBuildMetadata(string publicApi) => Regex.Replace(
        publicApi,
        "\\[assembly: System\\.Reflection\\.AssemblyMetadata\\(\"Verify\\.\\w+\", .*?\\)\\]\r?\n",
        string.Empty,
        RegexOptions.Singleline);
}
