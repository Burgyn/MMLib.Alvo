using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Host.Tests;

/// <summary>The dashboard's restatements of the catalog against the core (spec E13).</summary>
public sealed class FunctionOfferAgreementTests
{
    public static TheoryData<string> BuiltIns() => [.. CelFunctionCatalog.BuiltIns.Names];

    [Fact]
    public void The_dashboard_writes_every_signature_as_the_core_does() =>
        CelFunctionCatalog.BuiltIns.Functions.ShouldAllBe(function => FunctionOffer.Signature(function.Describe()) == function.Signature());

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void Every_template_compiles_once_its_placeholders_are_values(string name)
    {
        var offered = FunctionOffer.For(CelFunctionCatalog.BuiltIns.Describe(), CelProfile.Mutate).Single(f => f.Name == name);
        var source = offered.Parameters.Aggregate(
            FunctionOffer.Template(offered, firstArgument: null).Text,
            (text, parameter) => ReplaceFirst(text, parameter.Name, Value(parameter.Type)));

        new CelCompiler().Compile(source, CelProfile.Mutate, Probe).Errors.ShouldBeEmpty(source);
    }

    private static string Value(CelValueType type) => type switch
    {
        CelValueType.String => "new.text_field",
        CelValueType.Int => "1",
        CelValueType.Decimal => "new.money",
        CelValueType.Bool => "true",
        CelValueType.Timestamp => "new.moment",
        CelValueType.Uuid => "new.ident",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static string ReplaceFirst(string text, string placeholder, string value)
    {
        var at = text.IndexOf(placeholder, text.IndexOf('(', StringComparison.Ordinal), StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), value, text.AsSpan(at + placeholder.Length));
    }

    private static EntitySchema Probe { get; } = new()
    {
        Name = "probes",
        Fields =
        [
            new FieldSchema { Name = "text_field", Type = FieldType.String, MaxLength = 200, Nullable = true },
            new FieldSchema { Name = "money", Type = FieldType.Decimal, Precision = 18, Scale = 2, Nullable = true },
            new FieldSchema { Name = "moment", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "ident", Type = FieldType.Uuid, Nullable = true },
        ],
    };
}
