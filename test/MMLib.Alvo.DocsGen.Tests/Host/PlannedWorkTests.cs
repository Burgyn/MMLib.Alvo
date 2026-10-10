using MMLib.Alvo.DocsGen.Host;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public class PlannedWorkTests
{
    [Theory]
    [InlineData("bounds nothing (F7)", "bounds nothing (planned)")]
    [InlineData("no driver (F7, #41), so", "no driver (planned, #41), so")]
    [InlineData("has none (#38, F7) — whatever", "has none (planned, #38) — whatever")]
    [InlineData("oidc sign-in are #36 (F7), so", "oidc sign-in are planned (#36), so")]
    [InlineData("Custom functions are an F4 concern and the schema", "Custom functions are planned and the schema")]
    public void A_phase_code_becomes_planned_and_the_issue_stays(string text, string expected) =>
        PlannedWork.ForReaders(text).ShouldBe(expected);

    [Theory]
    [InlineData("nor is the payload projected per endpoint (#152)")]
    [InlineData("a receiver cannot yet verify the sender (7.1)")]
    [InlineData("Press F5 to reload.")]
    public void Text_without_a_phase_parenthetical_is_left_alone(string text) =>
        PlannedWork.ForReaders(text).ShouldBe(text);

    [Fact]
    public void Capability_lines_are_rewritten_on_the_page()
    {
        var page = CapabilitiesRenderer.Render(System.Text.Json.Nodes.JsonNode.Parse("""
            { "honoured": [], "warned": [ { "block": "functions", "consequence": "Nothing runs (F7)." } ], "refused": [] }
            """)!);

        page.Content.ShouldContain("- `functions` — Nothing runs (planned).\n");
    }
}
