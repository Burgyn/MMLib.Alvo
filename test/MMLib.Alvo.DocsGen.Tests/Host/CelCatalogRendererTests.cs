using MMLib.Alvo.DocsGen.Host;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public class CelCatalogRendererTests
{
    private const string Answer = """
        {
          "functions": [
            {
              "name": "trim",
              "parameters": [ { "name": "text", "type": "String", "acceptsNull": true } ],
              "result": "String", "resultMayBeNull": true, "summary": "Removes spaces.",
              "provenance": "BuiltIn", "profiles": [ "Condition", "Mutate" ]
            },
            {
              "name": "slug",
              "parameters": [ { "name": "text", "type": "String", "acceptsNull": false } ],
              "result": "String", "resultMayBeNull": false, "summary": "A host function.",
              "provenance": "Host", "profiles": [ "Condition", "Mutate" ]
            },
            {
              "name": "math.round",
              "parameters": [ { "name": "x", "type": "Decimal", "acceptsNull": false }, { "name": "digits", "type": "Int", "acceptsNull": false } ],
              "result": "Decimal", "resultMayBeNull": false, "summary": "Rounds half away from zero.",
              "provenance": "BuiltIn", "profiles": [ "Condition", "Mutate" ]
            }
          ]
        }
        """;

    [Fact]
    public void The_page_renders_one_row_per_overload()
    {
        var page = CelCatalogRenderer.Render(JsonNode.Parse(Answer)!);

        page.RelativePath.ShouldBe("cel-functions.md");
        page.Root.ShouldBe(OutputRoot.Reference);
        page.Content.ShouldContain("""
            | Function | Signature | Returns | Profiles | Summary |
            |---|---|---|---|---|
            | `math.round` | `math.round(x: Decimal, digits: Int)` | `Decimal` | Condition, Mutate | Rounds half away from zero. |
            | `trim` | `trim(text: String?)` | `String?` | Condition, Mutate | Removes spaces. |

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Host_functions_are_left_out() =>
        CelCatalogRenderer.Render(JsonNode.Parse(Answer)!).Content.ShouldNotContain("slug");

    [Fact]
    public void The_intro_states_the_source_and_the_null_legend()
    {
        var content = CelCatalogRenderer.Render(JsonNode.Parse(Answer)!).Content;

        content.ShouldStartWith("---\ntitle: \"CEL functions\"");
        content.ShouldContain("generated from `GET {management}/projects/{project}/cel/functions` on a host with no `AddCelFunction` registrations");
        content.ShouldContain("/MMLib.Alvo/concepts/cel/");
        content.ShouldContain("/MMLib.Alvo/guides/custom-cel-functions/");
        content.ShouldContain("`?`");
    }

    [Fact]
    public void An_unknown_profile_is_refused()
    {
        var answer = JsonNode.Parse(Answer)!;
        answer["functions"]![0]!["profiles"] = new JsonArray("Telepathy");

        Should.Throw<InvalidOperationException>(() => CelCatalogRenderer.Render(answer));
    }
}
