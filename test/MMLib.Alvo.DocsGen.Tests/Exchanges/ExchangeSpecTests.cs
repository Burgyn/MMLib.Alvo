using MMLib.Alvo.DocsGen.Exchanges;

namespace MMLib.Alvo.DocsGen.Tests.Exchanges;

public class ExchangeSpecTests
{
    public static TheoryData<string> Specs()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "website", "src", "exchanges");
        var specs = Directory.EnumerateFiles(root, "*.exchange.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        specs.ShouldNotBeEmpty("website/src/exchanges must hold at least one spec");
        return [.. specs];
    }

    [Theory]
    [MemberData(nameof(Specs))]
    public void Every_spec_parses_and_names_a_runnable_descriptor(string path)
    {
        var spec = ExchangeSpec.Parse(ExchangesGenerator.NameOf(path), File.ReadAllText(path));
        var descriptor = Path.Combine(RepositoryRoot.Find(), spec.Descriptor);

        File.Exists(descriptor).ShouldBeTrue($"{spec.Name}: {spec.Descriptor} does not exist");
        AlvoExamples.IsMarkedNotRunnable(descriptor).ShouldBeFalse($"{spec.Name}: {spec.Descriptor} is marked not runnable");
        spec.Steps.ShouldAllBe(step => step.Key == null || spec.Keys.ContainsKey(step.Key));
    }

    [Fact]
    public void An_unknown_member_throws_naming_it() =>
        Should.Throw<InvalidOperationException>(() => ExchangeSpec.Parse("fixture/typo", """
            { "descriptor": "d.alvo.json", "keys": {}, "steps": [ { "method": "GET", "path": "/api/x", "expcet": 200, "expect": 200 } ] }
            """)).Message.ShouldBe("fixture/typo: unknown member 'expcet' in step 0");

    [Theory]
    [InlineData("X-Alvo-Api-Key", "'key'")]
    [InlineData("content-type", "'contentType'")]
    public void A_header_the_runner_owns_is_refused(string header, string member) =>
        Should.Throw<InvalidOperationException>(() => ExchangeSpec.Parse("fixture/owned", $$"""
            { "descriptor": "d.alvo.json", "keys": {},
              "steps": [ { "method": "POST", "path": "/api/x", "body": {}, "headers": { "{{header}}": "x" }, "expect": 201 } ] }
            """)).Message.ShouldContain(member);

    [Fact]
    public void A_content_type_without_a_body_is_refused() =>
        Should.Throw<InvalidOperationException>(() => ExchangeSpec.Parse("fixture/nobody", """
            { "descriptor": "d.alvo.json", "keys": {},
              "steps": [ { "method": "GET", "path": "/api/x", "contentType": "text/plain", "expect": 200 } ] }
            """)).Message.ShouldBe("fixture/nobody: step 0 sets 'contentType' but sends no 'body' or 'bodyFile'");

    [Fact]
    public void A_body_file_member_without_a_body_file_is_refused() =>
        Should.Throw<InvalidOperationException>(() => ExchangeSpec.Parse("fixture/noFile", """
            { "descriptor": "d.alvo.json", "keys": {},
              "steps": [ { "method": "PUT", "path": "/api/x", "bodyFileAs": "descriptorJson", "expect": 200 } ] }
            """)).Message.ShouldBe("fixture/noFile: step 0 sets 'bodyFileAs' but names no 'bodyFile'");

    [Fact]
    public void A_body_beside_a_body_file_needs_a_member_to_merge_into() =>
        Should.Throw<InvalidOperationException>(() => ExchangeSpec.Parse("fixture/both", """
            { "descriptor": "d.alvo.json", "keys": {},
              "steps": [ { "method": "POST", "path": "/api/x", "body": { "a": 1 }, "bodyFile": "f.json", "expect": 200 } ] }
            """)).Message.ShouldBe("fixture/both: step 0 sends both 'body' and 'bodyFile'; name the file's member with 'bodyFileAs' to merge them");

    [Theory]
    [InlineData("""[1]""")]
    [InlineData("""{ "descriptorJson": "x" }""")]
    public void A_merged_body_must_be_an_object_without_the_file_member(string body) =>
        Should.Throw<InvalidOperationException>(() => ExchangeSpec.Parse("fixture/clash", $$"""
            { "descriptor": "d.alvo.json", "keys": {},
              "steps": [ { "method": "POST", "path": "/api/x", "body": {{body}}, "bodyFile": "f.json", "bodyFileAs": "descriptorJson", "expect": 200 } ] }
            """)).Message.ShouldContain("'body' must be an object without that member");

    [Fact]
    public void A_missing_expectation_throws() =>
        Should.Throw<InvalidOperationException>(() => ExchangeSpec.Parse("fixture/missing", """
            { "descriptor": "d.alvo.json", "keys": {}, "steps": [ { "method": "GET", "path": "/api/x" } ] }
            """)).Message.ShouldContain("'expect'");

    [Fact]
    public void A_key_defaults_its_secret_variable()
    {
        var spec = ExchangeSpec.Parse("fixture/keys", """
            { "descriptor": "d.alvo.json",
              "keys": { "agent": { "roles": ["authenticated"], "scopes": ["*:read"] },
                        "admin": { "roles": ["admin"], "scopes": [], "secretVariable": "ALVO_ADMIN_KEY_SECRET" } },
              "steps": [] }
            """);

        spec.Keys["agent"].SecretVariable.ShouldBe("ALVO_KEY_SECRET");
        spec.Keys["admin"].SecretVariable.ShouldBe("ALVO_ADMIN_KEY_SECRET");
    }

    [Fact]
    public void The_name_is_the_path_under_exchanges_without_the_suffix() =>
        ExchangesGenerator.NameOf(Path.Combine(RepositoryRoot.Find(), "website", "src", "exchanges", "landing", "hero.exchange.json"))
            .ShouldBe("landing/hero");
}
