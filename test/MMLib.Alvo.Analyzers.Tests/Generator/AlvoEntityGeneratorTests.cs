using Microsoft.CodeAnalysis;
using System.Text.Json;
using System.Xml.Linq;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>Which files the generator reads, where it puts what it generates, and what it reports when it cannot.</summary>
public sealed class AlvoEntityGeneratorTests
{
    [Fact]
    public void A_file_without_the_metadata_generates_nothing()
    {
        var run = GeneratorDriverHarness.Run(
            new InputFile("help-desk.alvo.json", Descriptors.HelpDesk, Descriptor: null),
            new InputFile("other.alvo.json", Descriptors.HelpDesk, Descriptor: "false"));

        run.Results.Single().GeneratedSources.ShouldBeEmpty();
        run.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void The_metadata_is_case_insensitive() =>
        GeneratorDriverHarness.Sources(GeneratorDriverHarness.Run(new InputFile("help-desk.alvo.json", Descriptors.HelpDesk, "TRUE")))
            .Select(source => source.Hint).ShouldBe(["Consumer.HelpDeskRoles.g.cs", "Consumer.Ticket.g.cs"]);

    [Fact]
    public void AlvoNamespace_overrides_the_root_namespace()
    {
        var sources = GeneratorDriverHarness.Sources(GeneratorDriverHarness.Run(new InputFile("help-desk.alvo.json", Descriptors.HelpDesk, Namespace: "Desk.Model")));

        sources.Select(source => source.Hint).ShouldBe(["Desk.Model.HelpDeskRoles.g.cs", "Desk.Model.Ticket.g.cs"]);
        sources.ShouldAllBe(source => source.Text.Contains("namespace Desk.Model;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_AlvoNamespace_is_treated_as_absent(string @namespace) =>
        GeneratorDriverHarness.Sources(GeneratorDriverHarness.Run(new InputFile("help-desk.alvo.json", Descriptors.HelpDesk, Namespace: @namespace)))
            .Select(source => source.Hint).ShouldBe(["Consumer.HelpDeskRoles.g.cs", "Consumer.Ticket.g.cs"]);

    [Fact]
    public void Without_a_root_namespace_the_types_go_in_Alvo_Generated() =>
        GeneratorDriverHarness.Sources(GeneratorDriverHarness.Run(rootNamespace: null, new InputFile("help-desk.alvo.json", Descriptors.HelpDesk)))
            .Select(source => source.Hint).ShouldBe(["Alvo.Generated.HelpDeskRoles.g.cs", "Alvo.Generated.Ticket.g.cs"]);

    [Fact]
    public void A_snapshot_and_its_descriptor_generate_identical_sources()
    {
        var snapshot = JsonSerializer.Serialize(new { project = "help-desk", revision = 3, descriptorJson = Descriptors.HelpDesk });

        var fromSnapshot = GeneratorDriverHarness.Sources(GeneratorDriverHarness.Run(new InputFile("snapshot.json", snapshot)));
        var fromDescriptor = GeneratorDriverHarness.Sources(GeneratorDriverHarness.Run(new InputFile("help-desk.alvo.json", Descriptors.HelpDesk)));

        fromSnapshot.ShouldBe(fromDescriptor);
    }

    [Fact]
    public void Malformed_json_reports_ALVO2001_at_its_line_and_column()
    {
        var run = GeneratorDriverHarness.Run(new InputFile("/repo/broken.alvo.json", "{\n  \"apiVersion\": ,\n}"));

        var diagnostic = run.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("ALVO2001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'broken.alvo.json' is not an Alvo descriptor the generator can read: The JSON is malformed at line 2, column 17");
        var span = diagnostic.Location.GetLineSpan();
        span.Path.ShouldBe("/repo/broken.alvo.json");
        span.StartLinePosition.ShouldBe(new Microsoft.CodeAnalysis.Text.LinePosition(1, 16));
        run.Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void A_foreign_api_version_reports_ALVO2001_without_a_position()
    {
        var run = GeneratorDriverHarness.Run(new InputFile("old.alvo.json", """{ "apiVersion": "alvo.dev/v0", "entities": {} }"""));

        var diagnostic = run.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("ALVO2001");
        diagnostic.Location.ShouldBe(Location.None);
    }

    [Fact]
    public void Two_descriptors_generating_one_class_report_ALVO2002_and_keep_the_first()
    {
        var other = Descriptors.With("""{ "tickets": { "fields": { "subject": { "type": "string" } } } }""", name: "other-desk");

        var run = GeneratorDriverHarness.Run(new InputFile("b/other.alvo.json", other), new InputFile("a/help-desk.alvo.json", Descriptors.HelpDesk));

        var diagnostic = run.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("ALVO2002");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldBe(
            "'other.alvo.json' would generate 'Consumer.Ticket', which another entity or descriptor already generates; it is skipped. Rename the entity, or set AlvoNamespace on one of the descriptors.");
        var sources = GeneratorDriverHarness.Sources(run);
        sources.Select(source => source.Hint).ShouldBe(["Consumer.HelpDeskRoles.g.cs", "Consumer.OtherDeskRoles.g.cs", "Consumer.Ticket.g.cs"]);
        sources.Single(source => source.Hint == "Consumer.Ticket.g.cs").Text.ShouldContain("project 'help-desk'");
    }

    [Fact]
    public void An_entity_whose_class_another_entity_s_enum_already_takes_is_skipped_with_ALVO2002()
    {
        var descriptor = Descriptors.With("""
            { "tickets": { "fields": { "status": { "type": "enum", "values": ["open"] } } },
              "ticket_statuses": { "fields": { "label": { "type": "string" } } } }
            """);

        var run = GeneratorDriverHarness.Run(new InputFile("desk.alvo.json", descriptor));

        run.Diagnostics.ShouldHaveSingleItem().GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("'Consumer.TicketStatus'");
        GeneratorDriverHarness.Sources(run).Select(source => source.Hint).ShouldBe(["Consumer.HelpDeskRoles.g.cs", "Consumer.Ticket.g.cs"]);
    }

    [Fact]
    public void Every_entity_class_carries_its_project_marker()
    {
        var ticket = GeneratorDriverHarness.Sources(GeneratorDriverHarness.Run(new InputFile("help-desk.alvo.json", Descriptors.HelpDesk)))
            .Single(source => source.Hint == "Consumer.Ticket.g.cs").Text;

        ticket.ShouldContain("[assembly: global::System.Reflection.AssemblyMetadata(\"MMLib.Alvo.Analyzers.Project:Consumer.Ticket\", \"help-desk\")]");
    }

    [Fact]
    public void The_package_props_expose_the_metadata()
    {
        var root = RepositoryRoot.Find();
        var props = XDocument.Load(Path.Combine(root, "src", "MMLib.Alvo", "build", "MMLib.Alvo.props"));

        props.Descendants("CompilerVisibleItemMetadata").Select(item => ((string?)item.Attribute("Include"), (string?)item.Attribute("MetadataName")))
            .ShouldBe([("AdditionalFiles", "AlvoDescriptor"), ("AdditionalFiles", "AlvoNamespace")]);
        props.Descendants("CompilerVisibleProperty").Select(item => (string?)item.Attribute("Include")).ShouldBe(["RootNamespace"]);
        XDocument.Load(Path.Combine(root, "src", "MMLib.Alvo", "MMLib.Alvo.csproj")).Descendants("None")
            .ShouldContain(item => (string?)item.Attribute("Include") == "build/MMLib.Alvo.props"
                && (string?)item.Attribute("Pack") == "true" && (string?)item.Attribute("PackagePath") == "build/");
    }
}
