using MMLib.Alvo.DocsGen.Host;
using MMLib.Alvo.DocsGen.Xml;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public class ManagementApiRendererTests
{
    private const string Docs = """
        <doc><members>
          <member name="M:MMLib.Alvo.Management.IAlvoManagement.GetCelFunctionsAsync(System.String,System.Threading.CancellationToken)">
            <summary>Lists every CEL function a descriptor may call.</summary>
          </member>
          <member name="M:MMLib.Alvo.IAlvoUserAdministration.ListAsync(MMLib.Alvo.AlvoUserQuery,System.Threading.CancellationToken)">
            <summary>Lists the people who can sign in.</summary>
          </member>
          <member name="M:MMLib.Alvo.Management.IAlvoManagement.GetInfoAsync(System.Threading.CancellationToken)">
            <summary></summary>
          </member>
        </members></doc>
        """;

    [Fact]
    public void Routes_are_grouped_and_summarised()
    {
        var page = ManagementApiRenderer.Render([new("GET", "/management/projects/{project}/cel/functions", "GetCelFunctionsAsync")], XmlDocs.Parse(Docs));

        page.RelativePath.ShouldBe("management-api.md");
        page.Content.ShouldContain("| GET | `/management/projects/{project}/cel/functions` | `GetCelFunctionsAsync` | Lists every CEL function a descriptor may call. |\n");
    }

    [Fact]
    public void Each_interface_gets_its_own_section()
    {
        var content = ManagementApiRenderer.Render(
            [
                new("GET", "/management/projects/{project}/users", "ListAsync"),
                new("GET", "/management/projects/{project}/cel/functions", "GetCelFunctionsAsync"),
            ],
            XmlDocs.Parse(Docs)).Content;

        content.IndexOf("## IAlvoManagement", StringComparison.Ordinal).ShouldBeLessThan(content.IndexOf("## IAlvoUserAdministration", StringComparison.Ordinal));
        content.ShouldContain("| GET | `/management/projects/{project}/users` | `ListAsync` | Lists the people who can sign in. |\n");
    }

    [Fact]
    public void Rows_are_ordered_by_route_then_method()
    {
        var content = ManagementApiRenderer.Render(
            [
                new("PUT", "/management/projects/{project}/cel/functions", "GetCelFunctionsAsync"),
                new("GET", "/management/projects/{project}/cel/functions", "GetCelFunctionsAsync"),
            ],
            XmlDocs.Parse(Docs)).Content;

        content.IndexOf("| GET |", StringComparison.Ordinal).ShouldBeLessThan(content.IndexOf("| PUT |", StringComparison.Ordinal));
    }

    [Fact]
    public void The_intro_states_the_prefix_and_the_missing_openapi_document()
    {
        var content = ManagementApiRenderer.Render([], XmlDocs.Parse(Docs)).Content;

        content.ShouldContain("`/management`");
        content.ShouldContain("AlvoManagementOptions.RoutePrefix");
        content.ShouldContain("not in the OpenAPI document");
        content.ShouldContain("/start-here/coding-agents/");
    }

    [Fact]
    public void A_member_without_a_summary_is_refused() =>
        Should.Throw<InvalidOperationException>(() =>
            ManagementApiRenderer.Render([new("GET", "/management/info", "GetInfoAsync")], XmlDocs.Parse(Docs)));

    [Fact]
    public void A_member_of_neither_interface_is_refused() =>
        Should.Throw<InvalidOperationException>(() =>
            ManagementApiRenderer.Render([new("GET", "/management/nowhere", "NowhereAsync")], XmlDocs.Parse(Docs)));
}
