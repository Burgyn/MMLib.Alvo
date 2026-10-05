using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The catalog: name to overloads, built-ins first-class, host functions added once.</summary>
public sealed class CelFunctionCatalogTests
{
    private static CelFunction Host(string name) => new(
        name, [CelBuiltInFunctions.Parameter("s", CelValueType.String)], CelValueType.String, ResultNullable: true,
        "A host function.", IsHost: true, CelBuiltInFunctions.ConditionAndMutate, arguments => arguments[0]);

    private static IReadOnlyList<string> BuiltInNames => [.. CelBuiltInFunctions.All.Select(f => f.Name).Distinct().Order(StringComparer.Ordinal)];

    [Fact]
    public void The_built_ins_are_the_legacy_calls_and_the_five_functions()
    {
        var catalog = CelFunctionCatalog.BuiltIns;

        catalog.Names.ShouldBe(BuiltInNames);
        catalog.Overloads("math.abs").Select(o => o.ResultType).ShouldBe([CelValueType.Int, CelValueType.Decimal]);
        catalog.Overloads("math.round").Select(o => o.ResultType).ShouldBe([CelValueType.Int, CelValueType.Decimal, CelValueType.Decimal]);
        catalog.Functions.Where(f => f.IsLegacy).Select(f => f.Name).ShouldBe(["now"]);
        catalog.Functions.Where(f => !f.IsLegacy).ShouldAllBe(f => f.Profiles.SetEquals(new[] { CelProfile.Condition, CelProfile.Mutate }));
    }

    [Fact]
    public void Describe_is_ordinal_by_name_with_overloads_in_declaration_order()
    {
        var catalog = CelFunctionCatalog.BuiltIns.With([Host("Zeta"), Host("alpha")]);

        var described = catalog.Describe();

        described.Select(f => f.Name).ShouldBe([.. CelBuiltInFunctions.All.Select(f => f.Name).Append("Zeta").Append("alpha").Order(StringComparer.Ordinal)]);
        described.Where(f => f.Name == "math.abs").Select(f => f.Result).ShouldBe(catalog.Overloads("math.abs").Select(o => o.Describe().Result));
        described.Where(f => f.Name == "math.round").Select(f => f.Result).ShouldBe([CelValueType.Int, CelValueType.Decimal, CelValueType.Decimal]);
    }

    [Fact]
    public void An_unknown_name_has_no_overloads()
    {
        CelFunctionCatalog.BuiltIns.Contains("normalizePhone").ShouldBeFalse();
        CelFunctionCatalog.BuiltIns.Overloads("normalizePhone").ShouldBeEmpty();
    }

    [Fact]
    public void A_host_function_joins_the_built_ins_in_name_order()
    {
        var catalog = CelFunctionCatalog.BuiltIns.With([Host("normalizePhone")]);

        catalog.Names.ShouldBe([.. BuiltInNames.Append("normalizePhone").Order(StringComparer.Ordinal)]);
        catalog.Overloads("normalizePhone").ShouldHaveSingleItem().IsHost.ShouldBeTrue();
    }

    [Fact]
    public void A_host_function_may_not_reuse_a_catalogued_name() =>
        Should.Throw<InvalidOperationException>(() => CelFunctionCatalog.BuiltIns.With([Host("now")]))
            .Message.ShouldContain("'now'");

    [Fact]
    public void Two_host_functions_may_not_share_a_name() =>
        Should.Throw<InvalidOperationException>(() => CelFunctionCatalog.BuiltIns.With([Host("vat"), Host("vat")]))
            .Message.ShouldContain("'vat'");

    private static CelFunction BuiltIn(string name, CelValueType type, IReadOnlySet<CelProfile> profiles) => new(
        name, [CelBuiltInFunctions.Parameter("x", type)], type, ResultNullable: false,
        "A built-in.", IsHost: false, profiles, arguments => arguments[0]);

    /// <summary>
    /// The built-in overload comes first, so only the provenance half of the guard can refuse it: the "host declared
    /// twice" half looks at the first overload's provenance, which is built-in here.
    /// </summary>
    [Fact]
    public void One_name_may_not_mix_a_built_in_and_a_host_overload()
    {
        CelFunction[] mixed =
        [
            BuiltIn("vat", CelValueType.Int, CelBuiltInFunctions.ConditionAndMutate),
            Host("vat"),
        ];

        Should.Throw<InvalidOperationException>(() => new CelFunctionCatalog(mixed)).Message.ShouldContain("'vat'");
    }

    [Fact]
    public void One_name_may_not_mix_profiles_across_its_overloads()
    {
        CelFunction[] mixed =
        [
            BuiltIn("vat", CelValueType.Int, CelBuiltInFunctions.ConditionAndMutate),
            BuiltIn("vat", CelValueType.Decimal, CelBuiltInFunctions.MutateOnly),
        ];

        Should.Throw<InvalidOperationException>(() => new CelFunctionCatalog(mixed)).Message.ShouldContain("'vat'");
    }

    [Fact]
    public void Built_in_overloads_with_one_provenance_and_one_profile_set_share_a_name() =>
        new CelFunctionCatalog(
        [
            BuiltIn("vat", CelValueType.Int, CelBuiltInFunctions.ConditionAndMutate),
            BuiltIn("vat", CelValueType.Decimal, CelBuiltInFunctions.ConditionAndMutate),
        ]).Overloads("vat").Count.ShouldBe(2);

    [Fact]
    public void A_signature_names_every_parameter_its_type_and_nullability()
    {
        CelFunctionCatalog.BuiltIns.Overloads("lowerAscii").Single().Signature().ShouldBe("lowerAscii(text: String) -> String");
        CelFunctionCatalog.BuiltIns.Overloads("now").Single().Signature().ShouldBe("now() -> Timestamp");
    }
}
