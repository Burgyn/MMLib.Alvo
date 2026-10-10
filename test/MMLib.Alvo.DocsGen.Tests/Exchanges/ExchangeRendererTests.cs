using MMLib.Alvo.DocsGen.Exchanges;
using MMLib.Alvo.DocsGen.Host;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Exchanges;

public class ExchangeRendererTests
{
    private static readonly CapturedRequest _post = new("POST", "/api/tickets",
        [new("X-Alvo-Api-Key", "agent.$ALVO_KEY_SECRET"), new("Content-Type", "application/json")], """{"title":"It's  late"}""");

    [Fact]
    public void Curl_quotes_the_key_and_the_body() =>
        ExchangeRenderer.Curl(_post).ShouldBe(
            "curl -sS -X POST http://localhost:8080/api/tickets \\\n" +
            "  -H \"X-Alvo-Api-Key: agent.$ALVO_KEY_SECRET\" \\\n" +
            "  -H \"Content-Type: application/json\" \\\n" +
            "  -d '{\"title\":\"It'\\''s  late\"}'");

    [Fact]
    public void Curl_escapes_double_quotes_inside_a_header() =>
        ExchangeRenderer.Curl(new CapturedRequest("GET", "/api/tickets/abc", [new("If-None-Match", "\"r1\"")], null))
            .ShouldBe("curl -sS -X GET http://localhost:8080/api/tickets/abc \\\n  -H \"If-None-Match: \\\"r1\\\"\"");

    [Fact]
    public void Curl_quotes_a_url_whose_query_the_shell_would_read() =>
        ExchangeRenderer.Curl(new CapturedRequest("GET", "/api/vehicles?or=(color.eq.red,color.eq.blue)&model=ilike.%25a%25", [], null))
            .ShouldBe("curl -sS -X GET 'http://localhost:8080/api/vehicles?or=(color.eq.red,color.eq.blue)&model=ilike.%25a%25'");

    [Fact]
    public void The_request_is_an_http_message_with_indented_json() =>
        ExchangeRenderer.HttpRequest(_post).ShouldBe(
            "POST /api/tickets HTTP/1.1\nX-Alvo-Api-Key: agent.$ALVO_KEY_SECRET\nContent-Type: application/json\n\n{\n  \"title\": \"It's  late\"\n}");

    [Fact]
    public void The_response_is_an_http_message_with_indented_json() =>
        ExchangeRenderer.HttpResponse(new CapturedResponse(201, "Created", [new("Content-Type", "application/json")], """{"id":"abc"}"""))
            .ShouldBe("HTTP/1.1 201 Created\nContent-Type: application/json\n\n{\n  \"id\": \"abc\"\n}");

    [Fact]
    public void A_body_that_is_not_json_is_kept_verbatim() =>
        ExchangeRenderer.HttpResponse(new CapturedResponse(415, "Unsupported Media Type", [], "not json"))
            .ShouldBe("HTTP/1.1 415 Unsupported Media Type\n\nnot json");

    [Fact]
    public void The_page_carries_the_parsed_response_body()
    {
        var spec = ExchangeSpec.Parse("fixture/page", """
            { "descriptor": "d.alvo.json", "keys": {}, "steps": [ { "method": "GET", "path": "/api/tickets", "expect": 200 } ] }
            """);
        var step = new CapturedStep(new("GET", "/api/tickets", [], null), new(200, "OK", [], """{"items":[]}"""));

        var page = ExchangeRenderer.Render(spec, [step]);

        page.Root.ShouldBe(OutputRoot.Generated);
        page.RelativePath.ShouldBe("exchanges/fixture/page.json");
        var rendered = JsonNode.Parse(page.Content)!;
        rendered["name"]!.GetValue<string>().ShouldBe("fixture/page");
        rendered["steps"]![0]!["responseBody"]!["items"]!.AsArray().ShouldBeEmpty();
        rendered["steps"]![0]!["status"]!.GetValue<int>().ShouldBe(200);
    }

    [Fact]
    public async Task The_secret_never_reaches_the_output()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot.Find();
        var spec = ExchangeSpec.Parse("landing/hero", await File.ReadAllTextAsync(
            Path.Combine(root, "website", "src", "exchanges", "landing", "hero.exchange.json"), ct));
        await using var host = await HostBoot.StartAsync(Path.Combine(root, spec.Descriptor), spec.Keys, ct);

        var page = ExchangeRenderer.Render(spec, await ExchangeRunner.RunAsync(spec, host, root, ct));

        page.Content.ShouldNotContain(host.SecretOf("demo"));
        page.Content.ShouldContain("demo.$ALVO_DEMO_KEY_SECRET");
    }
}
