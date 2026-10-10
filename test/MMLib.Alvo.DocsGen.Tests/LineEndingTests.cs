using MMLib.Alvo.DocsGen.Llms;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests;

/// <summary>
/// The generated text is LF on every platform. A Windows checkout carries CRLF in every text file
/// (<c>.gitattributes</c> <c>* text=auto</c>), and <see cref="JsonSerializerOptions.NewLine"/> defaults to
/// <see cref="Environment.NewLine"/>, so either leak would only ever show on the Windows leg — these pin both on
/// every platform.
/// </summary>
public partial class LineEndingTests
{
    /// <summary>
    /// Read from the source rather than reflected: on macOS and Linux the default newline already is <c>\n</c>, so
    /// inspecting the live options could never fail anywhere but on Windows.
    /// </summary>
    [Fact]
    public void Every_indented_serializer_sets_an_lf_newline()
    {
        var tool = Path.Combine(RepositoryRoot.Find(), "tools", "MMLib.Alvo.DocsGen");
        var initializers = Directory.EnumerateFiles(tool, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(tool, file))
            .SelectMany(file => IndentedInitializer().Matches(File.ReadAllText(file)).Select(match => (File: Path.GetRelativePath(tool, file), match.Value)))
            .ToList();

        initializers.ShouldNotBeEmpty("the scan found no indented serializer at all, so it no longer measures anything");
        initializers.Where(found => !found.Value.Contains("NewLine = \"\\n\"", StringComparison.Ordinal)).Select(found => found.File)
            .ShouldBeEmpty("an indented serializer without NewLine = \"\\n\" writes CRLF on Windows");
    }

    [Fact]
    public void A_page_and_its_imports_checked_out_with_crlf_render_as_lf()
    {
        var docs = Directory.CreateTempSubdirectory("llms-crlf-").FullName;
        var generated = Path.Combine(docs, "..", "generated");
        Directory.CreateDirectory(Path.Combine(generated, "exchanges", "g"));
        Directory.CreateDirectory(Path.Combine(docs, "guides"));
        File.WriteAllText(Path.Combine(docs, "d.alvo.json"), "{\r\n  \"entities\": {\r\n    \"t\": { \"fields\": {} }\r\n  }\r\n}\r\n");
        File.WriteAllText(Path.Combine(docs, "Program.cs"), "class P\r\n{\r\n    void A()\r\n    {\r\n    }\r\n}\r\n");
        File.WriteAllText(Path.Combine(generated, "exchanges", "g", "one.json"),
            "{\r\n  \"steps\": [\r\n    { \"curl\": \"curl x\", \"httpRequest\": \"GET /x HTTP/1.1\", \"httpResponse\": \"HTTP/1.1 200 OK\\n\\n{}\", \"responseBody\": { \"id\": \"1\" } }\r\n  ]\r\n}\r\n");
        File.WriteAllText(Path.Combine(docs, "guides", "c.mdx"),
            "---\r\ntitle: C\r\ndescription: Does C.\r\n---\r\nimport d from '../d.alvo.json?raw';\r\nimport program from '../Program.cs?raw';\r\n\r\nIntro.\r\n\r\n"
            + "<JsonExcerpt code={d} pointer=\"/entities/t\" />\r\n\r\n<SourceExcerpt code={program} from=\"void A\" to=\"    }\" />\r\n\r\n"
            + "<Exchange name=\"g/one\" request={false} fields={[\"id\"]} />\r\n");

        var body = ContentPage.Read(Path.Combine(docs, "guides", "c.mdx"), docs, generated).Body;

        body.ShouldNotContain('\r');
        body.ShouldBe(
            "Intro.\n\n```json\n\"t\": {\n  \"fields\": {}\n}\n```\n\n```csharp\nvoid A()\n{\n}\n```\n\n"
            + "```http\nHTTP/1.1 200 OK\n\n{\n  \"id\": \"1\"\n}\n```");
    }

    private static bool IsBuildOutput(string tool, string file) =>
        Path.GetRelativePath(tool, file).Split(Path.DirectorySeparatorChar)[0] is "bin" or "obj";

    [GeneratedRegex(@"\{[^{}]*WriteIndented\s*=\s*true[^{}]*\}")]
    private static partial Regex IndentedInitializer();
}
