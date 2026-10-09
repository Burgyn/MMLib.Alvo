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
