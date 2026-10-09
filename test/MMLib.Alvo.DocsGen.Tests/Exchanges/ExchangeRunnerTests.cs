using MMLib.Alvo.DocsGen.Exchanges;

namespace MMLib.Alvo.DocsGen.Tests.Exchanges;

public class ExchangeRunnerTests
{
    [Fact]
    public async Task The_hero_spec_runs_against_a_real_host()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "website", "src", "exchanges", "landing", "hero.exchange.json");
        var spec = ExchangeSpec.Parse("landing/hero", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        var steps = await ExchangeRunner.RunAsync(spec, RepositoryRoot.Find(), TestContext.Current.CancellationToken);

        steps.Select(s => s.Response.Status).ShouldBe(spec.Steps.Select(s => s.Expect));
        steps[0].Response.Body!.ShouldContain("\"title\":\"Printer on fire\"");
        steps[0].Response.Body!.ShouldContain("\"priority\":\"normal\"");
        steps[0].Request.Headers.Select(h => h.Key).ShouldBe(["X-Alvo-Api-Key", "Content-Type"]);
        steps[1].Request.Headers.Select(h => h.Key).ShouldBe(["Content-Type"]);
        steps[1].Response.Body!.ShouldContain("\"detail\":\"The write was rejected by policy.\"");
        steps[1].Response.Body!.ShouldNotContain("traceId");
    }

    [Fact]
    public void The_trace_id_is_stripped_from_a_problem_only() =>
        (ExchangeRunner.WithoutVolatile("""{"type":"t","traceId":"0HN"}""", "application/problem+json"),
         ExchangeRunner.WithoutVolatile("""{"id":"a","traceId":"0HN"}""", "application/json"))
            .ShouldBe(("""{"type":"t"}""", """{"id":"a","traceId":"0HN"}"""));

    [Fact]
    public async Task A_wrong_expectation_fails_the_run()
    {
        var spec = ExchangeSpec.Parse("fixture/wrong", """
            { "descriptor": "website/src/snippets/landing/helpdesk.alvo.json",
              "keys": { "agent": { "roles": ["authenticated"], "scopes": ["*:read"] } },
              "steps": [ { "key": "agent", "method": "GET", "path": "/api/tickets", "expect": 418 } ] }
            """);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => ExchangeRunner.RunAsync(spec, RepositoryRoot.Find(), TestContext.Current.CancellationToken));
        thrown.Message.ShouldContain("fixture/wrong");
        thrown.Message.ShouldContain("418");
    }
}
