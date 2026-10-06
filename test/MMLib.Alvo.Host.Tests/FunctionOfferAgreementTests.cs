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

    /// <summary>Every built-in and probe field for which a mutate row prefills the call (each probe field type once).</summary>
    public static TheoryData<string, string> PrefilledBuiltIns() => [.. Prefilled()];

    public static TheoryData<string> ConditionBuiltIns() =>
        [.. FunctionOffer.For(CelFunctionCatalog.BuiltIns.Describe(), CelProfile.Condition).Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void Every_template_compiles_once_its_placeholders_are_values(string name)
    {
        var offered = Offered(name, CelProfile.Mutate);
        FunctionOffer.Template(offered, firstArgument: null).Text.ShouldBe(Call(offered, [.. offered.Parameters.Select(p => p.Name)]));

        var source = Call(offered, [.. offered.Parameters.Select(p => Value(p.Type))]);

        new CelCompiler().Compile(source, CelProfile.Mutate, Probe).Errors.ShouldBeEmpty(source);
    }

    /// <summary>Whenever a mutate row's field prefills a call (Ruling W-D: any overload's first parameter decides), the call compiles.</summary>
    [Theory]
    [MemberData(nameof(PrefilledBuiltIns))]
    public void Every_prefilled_template_compiles_once_its_other_placeholders_are_values(string name, string fieldName)
    {
        var offered = Offered(name, CelProfile.Mutate);
        var first = FunctionOffer.FirstArgument(offered, "beforeCreate", Probe.Fields.Single(f => f.Name == fieldName))!;

        FunctionOffer.Template(offered, first).Text.ShouldBe(Call(offered, [first, .. offered.Parameters.Skip(1).Select(p => p.Name)]));
        var source = Call(offered, [first, .. offered.Parameters.Skip(1).Select(p => Value(p.Type))]);

        new CelCompiler().Compile(source, CelProfile.Mutate, Probe).Errors.ShouldBeEmpty(source);
    }

    /// <summary>The prefill theory is not vacuous: every probe field type prefills some built-in, and W-D's own case is in it.</summary>
    [Fact]
    public void Every_probe_field_prefills_some_built_in()
    {
        var prefilled = Prefilled().ToList();

        prefilled.Select(pair => pair.Field).Distinct().ShouldBe(Probe.Fields.Select(f => f.Name), ignoreOrder: true);
        prefilled.ShouldContain(("math.ceil", "money"));
    }

    [Theory]
    [MemberData(nameof(ConditionBuiltIns))]
    public void Every_function_a_condition_offers_compiles_in_a_condition(string name)
    {
        var offered = Offered(name, CelProfile.Condition);
        var call = Call(offered, [.. offered.Parameters.Select(p => Value(p.Type))]);
        var source = $"{call} == {call}";

        new CelCompiler().Compile(source, CelProfile.Condition, Probe).Errors.ShouldBeEmpty(source);
    }

    private static IEnumerable<(string Name, string Field)> Prefilled() =>
        from offered in FunctionOffer.For(CelFunctionCatalog.BuiltIns.Describe(), CelProfile.Mutate)
        from field in Probe.Fields
        where FunctionOffer.FirstArgument(offered, "beforeCreate", field) is not null
        select (offered.Name, field.Name);

    private static OfferedFunction Offered(string name, CelProfile profile) =>
        FunctionOffer.For(CelFunctionCatalog.BuiltIns.Describe(), profile).Single(f => f.Name == name);

    private static string Call(OfferedFunction offered, IEnumerable<string> arguments) => $"{offered.Name}({string.Join(", ", arguments)})";

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

    private static EntitySchema Probe { get; } = new()
    {
        Name = "probes",
        Fields =
        [
            new FieldSchema { Name = "text_field", Type = FieldType.String, MaxLength = 200, Nullable = true },
            new FieldSchema { Name = "int_field", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "money", Type = FieldType.Decimal, Precision = 18, Scale = 2, Nullable = true },
            new FieldSchema { Name = "moment", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "ident", Type = FieldType.Uuid, Nullable = true },
        ],
    };
}
