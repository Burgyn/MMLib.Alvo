using MMLib.Alvo.DocsGen.Host;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public class HostCaptureTests
{
    [Fact]
    public async Task The_real_host_answers_all_four_sources()
    {
        var snapshot = await HostCapture.CaptureAsync(RepositoryRoot.Find(), TestContext.Current.CancellationToken);

        snapshot.OpenApi["openapi"]!.GetValue<string>().ShouldStartWith("3.1");
        snapshot.OpenApi["paths"]!.AsObject().Select(p => p.Key).ShouldContain("/api/owners");
        snapshot.CelFunctions["functions"]!.AsArray().Count.ShouldBeGreaterThan(10);
        snapshot.Capabilities["honoured"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldContain("entities");
        snapshot.ManagementRoutes.ShouldContain(r => r.Member == "GetCelFunctionsAsync" && r.Method == "GET");
    }
}
