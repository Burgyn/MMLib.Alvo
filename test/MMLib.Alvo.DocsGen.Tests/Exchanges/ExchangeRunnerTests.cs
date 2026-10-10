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
    public async Task A_body_merges_into_the_wrapped_body_file()
    {
        var spec = ExchangeSpec.Parse("fixture/merge", """
            { "descriptor": "d.alvo.json", "keys": {},
              "steps": [ { "method": "POST", "path": "/x", "bodyFile": "test/MMLib.Alvo.DocsGen.Tests/Exchanges/merge-body.fixture.json",
                           "bodyFileAs": "descriptorJson", "body": { "path": "/a", "source": "'b'" }, "expect": 200 } ] }
            """);

        var body = await ExchangeRunner.BodyOf(spec.Steps[0], RepositoryRoot.Find(), TestContext.Current.CancellationToken);

        var json = System.Text.Json.Nodes.JsonNode.Parse(body!)!.AsObject();
        json.Select(member => member.Key).ShouldBe(["descriptorJson", "path", "source"]);
        json["descriptorJson"]!.GetValue<string>().ShouldContain("merge-fixture");
        json["source"]!.GetValue<string>().ShouldBe("'b'");
    }

    [Fact]
    public async Task A_wrong_secret_is_presented_and_shown_for_the_key()
    {
        var spec = ExchangeSpec.Parse("fixture/wrong-secret", """
            { "descriptor": "website/src/snippets/landing/helpdesk.alvo.json",
              "keys": { "agent": { "roles": ["authenticated"], "scopes": ["*:read"] } },
              "steps": [ { "key": "agent", "method": "GET", "path": "/api/tickets", "wrongSecret": true, "expect": 401 },
                         { "key": "agent", "method": "GET", "path": "/api/tickets", "expect": 200 } ] }
            """);

        var steps = await ExchangeRunner.RunAsync(spec, RepositoryRoot.Find(), TestContext.Current.CancellationToken);

        steps.Select(s => s.Response.Status).ShouldBe([401, 200]);
        steps[0].Request.Headers.ShouldContain(new KeyValuePair<string, string>("X-Alvo-Api-Key", $"agent.{ExchangeRunner.WrongSecret}"));
        steps[1].Request.Headers.ShouldContain(new KeyValuePair<string, string>("X-Alvo-Api-Key", "agent.$ALVO_KEY_SECRET"));
    }

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
