using PublicApiGenerator;

namespace MMLib.Alvo.Tests.Api;

/// <summary>
/// Public API approval gate for EVERY packable MMLib.Alvo assembly. Linked into
/// each test project (like the architecture rules) and run against that project's
/// sibling assembly (<see cref="TestTarget"/>). A change to the public surface
/// fails until the <c>PublicApi.&lt;assembly&gt;.verified.txt</c> baseline is
/// consciously updated. Baselines live in <c>test/_shared/</c>
/// (<see cref="VerifyModuleInit"/> pins the directory).
/// </summary>
public class PublicApiApprovalTests
{
    /// <summary>
    /// Our DI extensions (<c>AddAlvo</c>, <c>UseSqlite</c>, <c>UsePostgreSql</c>,
    /// <c>FromDescriptor</c>, <c>UseSchemaPrefix</c>) live in
    /// <c>Microsoft.Extensions.DependencyInjection</c> per the extensibility rules
    /// (docs/architecture/extensibility.md rule 11). PublicApiGenerator's default
    /// <c>UseDenyNamespacePrefixesForExtensionMethods = true</c> hides extension
    /// methods declared in <c>Microsoft.*</c>/<c>System.*</c> namespaces, which
    /// would make a breaking change to that builder surface pass this gate
    /// silently. Disabling it keeps those extension methods visible.
    /// </summary>
    private static readonly ApiGeneratorOptions _options = new()
    {
        UseDenyNamespacePrefixesForExtensionMethods = false,
    };

    [Fact]
    public Task Public_api_has_not_changed()
    {
        var target = TestTarget.Resolve();

        return Verify(target.GeneratePublicApi(_options))
            .UseFileName($"PublicApi.{target.GetName().Name}");
    }
}
