using MMLib.Alvo.Descriptor;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>
/// The generated OpenAPI document holds Alvo's contract for <em>every</em> descriptor — sixteen generated
/// ones and the four the repository ships — and not only for the fixture the rules were written against.
/// </summary>
/// <remarks>
/// <para>
/// This is #26's actual claim. "Works for the demo, breaks on another combination of fields" is the typical
/// hole in a metadata-driven framework, and a suite that measured one fixture could not tell the difference.
/// Both halves run over each document: the static one is <c>schema/openapi-ruleset.yaml</c> through Vacuum,
/// and the conditional one is <see cref="OpenApiDocumentFacts"/> — the same implementation
/// <c>MMLib.Alvo.Api.Tests</c> runs over its own fixture.
/// </para>
/// <para>
/// The four <c>examples/</c> descriptors are here for a second reason: spec §415 asks for the lint to run
/// "against the demo's OpenAPI", and those four are what the compose stacks actually serve.
/// </para>
/// </remarks>
public class DocumentContractTests
{
    /// <summary>A world that serves its OpenAPI document, which is what both halves read.</summary>
    private static readonly AlvoApiWorldSetup _documented = new(MapOpenApiDocument: true);

    /// <summary>The sixteen committed seeds as theory data.</summary>
    public static TheoryData<int> Seeds => [.. GeneratedProject.Seeds];

    /// <summary>The shipped descriptors, discovered from the repository rather than listed here.</summary>
    /// <remarks>
    /// Discovered so that a fifth example cannot be added without this suite noticing it, and so that a
    /// renamed one fails loudly instead of quietly dropping out of the corpus.
    /// </remarks>
    public static TheoryData<string> Examples => [.. Shipped().Where(path => path != Unappliable)];

    /// <summary>
    /// The one shipped example this build cannot apply, named so its absence is a decision and not a gap.
    /// </summary>
    /// <remarks>
    /// <c>complex-crm</c> declares <c>field.default</c> on three fields, which the descriptor validator
    /// refuses because the value would be silently dropped. It is schema-valid and un-appliable at once —
    /// <c>ExamplesTests</c> checks only the schema — and <see cref="A_shipped_example_that_cannot_be_applied_is_refused_for_a_known_reason"/>
    /// pins exactly that, so this exclusion fails the moment the example becomes appliable or starts failing
    /// for some other reason. Tracked in #208.
    /// </remarks>
    private static string Unappliable { get; } =
        Path.Combine(RepositoryRoot.Find(), "examples", "complex-crm", "crm.alvo.json");

    /// <summary>A generated project's document holds both halves of the contract.</summary>
    /// <param name="seed">The project's seed, which is how the case is reproduced.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task A_generated_projects_document_holds_the_contract(int seed)
    {
        var project = GeneratedProject.Generate(seed);
        await using var world = await project.StartAsync([project.Admin()], _documented);
        var document = await world.OpenApiDocumentAsync();

        var violations = VacuumRunner.Lint(document, project.Name);

        violations.ShouldBeEmpty($"seed {seed}: {string.Join("; ", violations)}{Environment.NewLine}{project.Json}");
        OpenApiDocumentFacts.AssertShape(document, project.Entities);
    }

    /// <summary>A shipped example's document holds both halves too.</summary>
    /// <param name="descriptor">The example descriptor's path.</param>
    [Theory]
    [MemberData(nameof(Examples))]
    public async Task A_shipped_examples_document_holds_the_contract(string descriptor)
    {
        var name = Path.GetFileNameWithoutExtension(descriptor).Replace(".alvo", string.Empty, StringComparison.Ordinal);
        await using var world = await AlvoApiWorld.FromDescriptorPathAsync(descriptor, [Admin], _documented);
        var document = await world.OpenApiDocumentAsync();

        var violations = VacuumRunner.Lint(document, name);

        violations.ShouldBeEmpty($"{name}: {string.Join("; ", violations)}");
        OpenApiDocumentFacts.AssertShape(document, Entities(descriptor));
    }

    /// <summary>
    /// The example that cannot be applied is refused, and refused for the reason this suite excludes it for.
    /// </summary>
    /// <remarks>
    /// A silent exclusion is how a corpus quietly stops covering something. This makes the exclusion a fact:
    /// if <c>complex-crm</c> becomes appliable, or begins failing for a different reason, this goes red and
    /// whoever changed it decides whether the example belongs back in the corpus above.
    /// </remarks>
    [Fact]
    public async Task A_shipped_example_that_cannot_be_applied_is_refused_for_a_known_reason()
    {
        var failure = await Should.ThrowAsync<DescriptorValidationException>(
            () => AlvoApiWorld.FromDescriptorPathAsync(Unappliable, [Admin], _documented));

        failure.Message.ShouldContain(
            "Field 'default' is not honoured yet",
            Case.Insensitive,
            "#208: complex-crm is excluded from the corpus for this reason and no other");
    }

    /// <summary>Every positive example the repository ships, discovered rather than listed.</summary>
    /// <remarks>
    /// The count is pinned so a fifth example cannot be added without this suite noticing, and a renamed one
    /// fails loudly instead of quietly dropping out of the corpus.
    /// </remarks>
    private static List<string> Shipped()
    {
        var found = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "examples"), "*.alvo.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}_negative{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        found.Count.ShouldBe(4, "the repository ships four positive examples; adjust this suite when that changes");

        return found;
    }

    /// <summary>A key carrying every scope, so nothing here turns on authorization.</summary>
    private static TestApiKey Admin => new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>The entities a descriptor declares, read from the descriptor rather than from the document.</summary>
    /// <remarks>
    /// Read from the descriptor on purpose: the document is what is under test, so taking the expected set
    /// out of it would make the path-set claim compare the document against itself.
    /// </remarks>
    private static List<string> Entities(string descriptor) =>
    [
        .. JsonNode.Parse(File.ReadAllText(descriptor))!["entities"]!.AsObject().Select(entity => entity.Key)
    ];
}
