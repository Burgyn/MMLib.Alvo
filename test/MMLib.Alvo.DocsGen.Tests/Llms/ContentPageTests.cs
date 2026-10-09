using MMLib.Alvo.DocsGen.Llms;

namespace MMLib.Alvo.DocsGen.Tests.Llms;

public class ContentPageTests
{
    [Fact]
    public void Raw_snippet_imports_are_inlined_and_components_dropped()
    {
        var docs = Directory.CreateTempSubdirectory("llms-").FullName;
        Directory.CreateDirectory(Path.Combine(docs, "guides"));
        var snippets = Path.Combine(docs, "..", "snippets");
        Directory.CreateDirectory(snippets);
        File.WriteAllText(Path.Combine(snippets, "a.alvo.json"), "{ \"name\": \"a\" }");
        File.WriteAllText(Path.Combine(docs, "guides", "x.mdx"), """
            ---
            title: X guide
            description: Does X.
            ---
            import { Code, Tabs, TabItem } from '@astrojs/starlight/components';
            import a from '../../snippets/a.alvo.json?raw';

            Intro.

            <Tabs>
            <TabItem label="One">
            <Code code={a} lang="json" title="a.alvo.json" />
            </TabItem>
            </Tabs>
            """);

        var page = ContentPage.Read(Path.Combine(docs, "guides", "x.mdx"), docs, Path.Combine(docs, "..", "generated"));

        page.Slug.ShouldBe("guides/x");
        page.Section.ShouldBe("guides");
        page.Title.ShouldBe("X guide");
        page.Body.ShouldBe("Intro.\n\n```json\n{ \"name\": \"a\" }\n```");
    }

    [Fact]
    public void Exchanges_and_json_excerpts_are_inlined()
    {
        var docs = Directory.CreateTempSubdirectory("llms-").FullName;
        var generated = Path.Combine(docs, "..", "generated");
        Directory.CreateDirectory(Path.Combine(generated, "exchanges", "g"));
        Directory.CreateDirectory(Path.Combine(docs, "..", "snippets"));
        Directory.CreateDirectory(Path.Combine(docs, "guides"));
        File.WriteAllText(Path.Combine(docs, "..", "snippets", "d.alvo.json"), "{ \"entities\": { \"t\": { \"fields\": {} } } }");
        File.WriteAllText(Path.Combine(generated, "exchanges", "g", "one.json"),
            "{ \"steps\": [ { \"curl\": \"curl -sS http://localhost:8080/api/t\", \"httpRequest\": \"GET /api/t HTTP/1.1\", \"httpResponse\": \"HTTP/1.1 200 OK\", \"status\": 200 } ] }");
        File.WriteAllText(Path.Combine(docs, "guides", "y.mdx"), """
            ---
            title: Y
            description: Does Y.
            ---
            import d from '../../snippets/d.alvo.json?raw';

            <JsonExcerpt code={d} pointer="/entities/t" />

            <Exchange name="g/one" />
            """);

        var page = ContentPage.Read(Path.Combine(docs, "guides", "y.mdx"), docs, generated);

        page.Body.ShouldBe("```json\n\"t\": {\n  \"fields\": {}\n}\n```\n\n```sh\ncurl -sS http://localhost:8080/api/t\n```\n\n```http\nHTTP/1.1 200 OK\n```");
    }

    [Fact]
    public void Source_excerpts_slice_between_anchors_and_dedent()
    {
        var docs = Directory.CreateTempSubdirectory("llms-").FullName;
        Directory.CreateDirectory(Path.Combine(docs, "guides"));
        File.WriteAllText(Path.Combine(docs, "Program.cs"), "class P\n{\n    void A()\n    {\n        B();\n    }\n}\n");
        File.WriteAllText(Path.Combine(docs, "guides", "z.mdx"), """
            ---
            title: "Z \"quoted\""
            description: Does Z.
            sidebar:
              order: 7
            ---
            import program from '../Program.cs?raw';

            {/* <!-- STUB-F6 --> */}
            <SourceExcerpt code={program} from="void A" to="    }" />
            """);

        var page = ContentPage.Read(Path.Combine(docs, "guides", "z.mdx"), docs, docs);

        page.Title.ShouldBe("Z \"quoted\"");
        page.Order.ShouldBe(7);
        page.Body.ShouldBe("```csharp\nvoid A()\n{\n    B();\n}\n```");
    }

    [Fact]
    public void An_index_page_takes_its_directory_slug()
    {
        var docs = Directory.CreateTempSubdirectory("llms-").FullName;
        Directory.CreateDirectory(Path.Combine(docs, "reference", "csharp"));
        File.WriteAllText(Path.Combine(docs, "reference", "csharp", "index.md"), "---\ntitle: C#\ndescription: D.\n---\n\nBody.\n");
        File.WriteAllText(Path.Combine(docs, "examples.md"), "---\ntitle: Examples\ndescription: E.\n---\n\nBody.\n");

        var index = ContentPage.Read(Path.Combine(docs, "reference", "csharp", "index.md"), docs, docs);
        var root = ContentPage.Read(Path.Combine(docs, "examples.md"), docs, docs);

        index.Slug.ShouldBe("reference/csharp");
        index.Section.ShouldBe("reference");
        index.Order.ShouldBe(int.MaxValue);
        root.Slug.ShouldBe("examples");
        root.Section.ShouldBe(string.Empty);
    }

    [Fact]
    public void Site_links_become_absolute_for_readers_outside_the_site()
    {
        var docs = Directory.CreateTempSubdirectory("llms-").FullName;
        File.WriteAllText(Path.Combine(docs, "x.md"), "---\ntitle: X\ndescription: D.\n---\n\nSee [Run](/MMLib.Alvo/start-here/run-your-own/) and [anchor](#a).\n");

        ContentPage.Read(Path.Combine(docs, "x.md"), docs, docs).Body
            .ShouldBe("See [Run](https://burgyn.github.io/MMLib.Alvo/start-here/run-your-own/) and [anchor](#a).");
    }

    [Fact]
    public void A_component_naming_an_unknown_import_fails()
    {
        var docs = Directory.CreateTempSubdirectory("llms-").FullName;
        File.WriteAllText(Path.Combine(docs, "x.mdx"), "---\ntitle: X\ndescription: D.\n---\n\n<Code code={missing} lang=\"json\" />\n");

        Should.Throw<InvalidOperationException>(() => ContentPage.Read(Path.Combine(docs, "x.mdx"), docs, docs));
    }
}
