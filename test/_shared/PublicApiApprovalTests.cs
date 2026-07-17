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
    [Fact]
    public Task Public_api_has_not_changed()
    {
        var target = TestTarget.Resolve();

        return Verify(target.GeneratePublicApi())
            .UseFileName($"PublicApi.{target.GetName().Name}");
    }
}
