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

    [Fact]
    public void An_example_with_its_own_stack_runs_the_command_its_readme_gives()
    {
        var fieldService = ExampleCatalog.Read(_root).Single(e => e.Directory == "field-service");
        var readme = File.ReadAllText(Path.Combine(_root, "examples", "field-service", "README.md"));
        var exported = System.Text.RegularExpressions.Regex.Matches(readme, @"export (ALVO_FS_\w+_SECRET)=").Select(m => m.Groups[1].Value);

        fieldService.MultiTenant.ShouldBeTrue();
        fieldService.OwnStack.ShouldNotBeNull();
        fieldService.OwnStack.SecretVariables.ShouldBe(exported);
        fieldService.OwnStack.Command().ShouldEndWith(
            "docker compose --env-file examples/field-service/demo-identities.env -f docker-compose.field-service.yml up --build --wait");
        ExampleCatalog.Read(_root).Where(e => e.Directory != "field-service").ShouldAllBe(e => e.OwnStack == null);
    }

    [Fact]
    public void The_page_runs_field_service_on_its_own_stack_and_says_it_is_multi_tenant()
    {
        var page = ExamplesGenerator.Render(ExampleCatalog.Read(_root)).Content;
        var section = page[page.IndexOf("## field-service", StringComparison.Ordinal)..page.IndexOf("## simple-tasks", StringComparison.Ordinal)];

        section.ShouldContain("**Tenancy:** multi-tenant");
        section.ShouldContain("-f docker-compose.field-service.yml up --build --wait");
        section.ShouldNotContain("ALVO_DESCRIPTOR=");
    }

    [Fact]
    public void The_page_carries_no_contributor_references_or_internal_terms()
    {
        var page = ExamplesGenerator.Render(ExampleCatalog.Read(_root)).Content;

        page.ShouldNotMatch(@"(?<![&\w])#\d+");
        page.ShouldNotContain("§");
        page.ShouldNotContain("číselník");
        page.ShouldNotContain("(D3)");
        page.ShouldNotContain("the analysis");
        page.ShouldNotContain("applies as it stands", Case.Insensitive);
    }

    [Fact]
    public void A_summary_loses_its_emphasis_markers_but_keeps_code()
    {
        ExamplesGenerator.Sentence("**applies as it stands.** a *format* showcase with `*:read` keys.")
            .ShouldBe("A format showcase with `*:read` keys.");
    }
}
