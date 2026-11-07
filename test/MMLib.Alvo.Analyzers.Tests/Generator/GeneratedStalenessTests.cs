using Microsoft.CodeAnalysis;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Schema;
using System.Reflection;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>The boot's staleness check over real generator output: silent on the descriptor it came from, loud on an older schema.</summary>
public sealed class GeneratedStalenessTests
{
    private static readonly string _newer = Descriptors.With(
        """{ "orders": { "fields": { "reference": { "type": "string", "required": true }, "discontinued_on": { "type": "date" } } } }""", name: "desk");

    private static readonly string _older = Descriptors.With(
        """{ "orders": { "fields": { "reference": { "type": "string", "required": true } } } }""", name: "desk");

    [Fact]
    public void Real_generator_output_from_a_newer_descriptor_is_stale_against_an_older_schema()
    {
        var types = Generated(new InputFile("desk.alvo.json", _newer));

        var stale = GeneratedEntityCheck.Stale(GeneratedEntityCheck.OfProject(types, "desk"), Schema(_older)).ShouldHaveSingleItem();

        stale.Shape.Type.FullName.ShouldBe("Consumer.Order");
        stale.Problems.ShouldHaveSingleItem().Message.ShouldContain("discontinued_on");
    }

    [Theory]
    [MemberData(nameof(AlvoEntityGeneratorSnapshotTests.Examples), MemberType = typeof(AlvoEntityGeneratorSnapshotTests))]
    public void Real_generator_output_fits_the_schema_of_the_example_it_came_from(string example)
    {
        var input = AlvoEntityGeneratorSnapshotTests.Input(example);
        var schema = AppliedSchema(input.Text);
        Assert.SkipWhen(schema is null, $"This build's mapper refuses the {example} example, so it has no applied schema to compare with.");
        var types = Generated(input);

        types.ShouldNotBeEmpty();
        GeneratedEntityCheck.Stale(types, schema!).Select(stale => $"{stale.Shape.Name}: {string.Join(" ", stale.Problems.Select(p => p.Message))}")
            .ShouldBeEmpty();
    }

    private static SchemaModel Schema(string descriptor) => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptor));

    private static SchemaModel? AppliedSchema(string descriptor)
    {
        try
        {
            return Schema(descriptor);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static IReadOnlyList<Type> Generated(InputFile input)
    {
        var (driver, compilation) = GeneratorDriverHarness.Create(GeneratorDriverHarness.RootNamespace, input);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        using var image = new MemoryStream();
        output.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success.ShouldBeTrue();
        var assembly = Assembly.Load(image.ToArray());

        return GeneratedEntityCheck.Discover(assembly);
    }
}
