using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.DocsGen.CSharp;
using System.Reflection;

namespace MMLib.Alvo.DocsGen.Tests.CSharp;

public class TypeNamesTests
{
    [Fact]
    public void Generic_and_nullable_parameters_read_as_csharp()
    {
        var method = typeof(AlvoBuilderExtensions).GetMethod(nameof(AlvoBuilderExtensions.AddCelFunction))!;

        Signatures.Of(method).ShouldBe(
            "public static IAlvoBuilder AddCelFunction(this IAlvoBuilder builder, string name, Delegate function, string? summary = null)");
    }

    [Theory]
    [InlineData(nameof(Shapes.Keyword), "int")]
    [InlineData(nameof(Shapes.NullableValue), "long?")]
    [InlineData(nameof(Shapes.NullableReference), "string?")]
    [InlineData(nameof(Shapes.Array), "byte[]")]
    [InlineData(nameof(Shapes.Generic), "IReadOnlyList<string?>")]
    [InlineData(nameof(Shapes.Dictionary), "IReadOnlyDictionary<string, object?>")]
    [InlineData(nameof(Shapes.Nested), "TypeNamesTests.Shapes.Inner")]
    public void Types_read_as_csharp(string property, string expected)
    {
        var info = typeof(Shapes).GetProperty(property)!;

        TypeNames.Display(info.PropertyType, new NullabilityInfoContext().Create(info)).ShouldBe(expected);
    }

    [Fact]
    public void Without_nullability_information_a_reference_type_reads_bare()
    {
        TypeNames.Display(typeof(List<string>), null).ShouldBe("List<string>");
    }

    public sealed class Shapes
    {
        public int Keyword { get; set; }

        public long? NullableValue { get; set; }

        public string? NullableReference { get; set; }

        public byte[] Array { get; set; } = [];

        public IReadOnlyList<string?> Generic { get; set; } = [];

        public IReadOnlyDictionary<string, object?> Dictionary { get; set; } = new Dictionary<string, object?>();

        public Inner Nested { get; set; } = new();

        public sealed class Inner;
    }
}
