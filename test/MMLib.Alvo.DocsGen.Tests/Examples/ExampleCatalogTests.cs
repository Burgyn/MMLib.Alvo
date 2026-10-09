using MMLib.Alvo.DocsGen.Examples;

namespace MMLib.Alvo.DocsGen.Tests.Examples;

public class ExampleCatalogTests
{
    private static readonly string _root = RepositoryRoot.Find();

    [Fact]
    public void Every_example_directory_is_listed_with_its_readme_bullet()
    {
        var directories = Directory.EnumerateDirectories(Path.Combine(_root, "examples"))
            .Select(Path.GetFileName).Where(name => name != "_negative").Order(StringComparer.Ordinal);

        var catalog = ExampleCatalog.Read(_root);

        catalog.Select(e => e.Directory).Order(StringComparer.Ordinal).ShouldBe(directories!);
        catalog.ShouldAllBe(e => e.Summary.Length > 0 && e.Entities.Count > 0);
        catalog.Single(e => e.Directory == "complex-crm").Runnable.ShouldBeFalse();
        catalog.Single(e => e.Directory == "vehicle-registry").Roles.ShouldBe(["inspector", "admin"]);
    }

    [Fact]
    public void Runnable_agrees_with_the_marker_the_test_corpus_reads()
    {
        foreach (var example in ExampleCatalog.Read(_root))
        {
            var descriptor = Path.Combine(_root, "examples", example.Directory, example.Descriptor);

            example.Runnable.ShouldBe(!AlvoExamples.IsMarkedNotRunnable(descriptor), example.Directory);
        }

        ExampleCatalog.NotRunnableMarker.ShouldBe(AlvoExamples.NotRunnableMarker);
    }

    [Fact]
    public void The_page_puts_runnable_examples_first_with_a_run_line()
    {
        var page = ExamplesGenerator.Render(ExampleCatalog.Read(_root)).Content;

        page.ShouldStartWith("---\ntitle: \"Examples\"\n");
        page.IndexOf("## simple-tasks", StringComparison.Ordinal).ShouldBeLessThan(page.IndexOf("## complex-crm", StringComparison.Ordinal));
        page.ShouldContain("ALVO_DESCRIPTOR=./examples/simple-tasks/tasks.alvo.json docker compose up --build --wait");
        page.ShouldContain("**Roles a key needs:** none declared; `authenticated` is enough");
        page.ShouldContain("**Applies:** no — [why](https://github.com/Burgyn/MMLib.Alvo/blob/main/examples/complex-crm/NOT-RUNNABLE.md)");
        page.ShouldNotContain("ALVO_DESCRIPTOR=./examples/complex-crm/");
    }
}
