using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Api;
using PublicApiGenerator;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The public-API approval gate for <c>MMLib.Alvo.Testing.EntityFrameworkCore</c>, the companion
/// test-support library that carries the <see cref="AlvoSqlDialectContractTests"/> seam and the T-SQL fake.
/// </summary>
/// <remarks>
/// <para>
/// It lives here rather than beside <c>TestingLibraryPublicApiTests</c> in <c>MMLib.Alvo.Tests</c> for the
/// same reason the companion project exists at all: <c>MMLib.Alvo.Tests</c> deliberately resolves no EF Core,
/// and loading this assembly there would put it back on that project's reference chain. This project already
/// references EF directly, so the baseline costs it nothing.
/// </para>
/// <para>
/// The library is not packable yet, so the shared <c>PublicApiApprovalTests</c> — which targets each test
/// project's own sibling assembly — never reaches it. The surface still matters: it is a contract suite an
/// out-of-repo driver author inherits, so a member appearing or disappearing is a new obligation or a dropped
/// one, and either should be a conscious act.
/// </para>
/// </remarks>
public class RelationalTestingLibraryPublicApiTests
{
    /// <summary>
    /// Xunit's Fact/Theory attributes capture the declaring source file's absolute path via
    /// <c>[CallerFilePath]</c>, which <c>PublicApiGenerator</c> would render into the baseline — making it
    /// differ per machine and per CI checkout rather than on a real API change. Same exclusion, and same
    /// reason, as <c>TestingLibraryPublicApiTests</c>.
    /// </summary>
    private static readonly ApiGeneratorOptions _options = new()
    {
        UseDenyNamespacePrefixesForExtensionMethods = false,
        ExcludeAttributes = ["Xunit.FactAttribute", "Xunit.TheoryAttribute"],
    };

    /// <summary>
    /// The Verify build-metadata strip is <b>not</b> optional here, even though this assembly declares no
    /// Verify dependency: <c>MMLib.Alvo.Testing</c>'s <c>Verify.XunitV3</c> reference flows to it, so it is
    /// stamped with absolute checkout paths all the same. Measured, not assumed — the first generated
    /// baseline carried four of them.
    /// </summary>
    [Fact]
    public Task Public_api_has_not_changed()
        => Verify(VerifyBuildMetadata.RemoveFrom(typeof(TSqlSqlDialect).Assembly.GeneratePublicApi(_options)))
            .UseFileName("PublicApi.MMLib.Alvo.Testing.EntityFrameworkCore");
}
