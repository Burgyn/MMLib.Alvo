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

    /// <summary>The one literal pin of the built-in names (preflight R-3): every other name list derives from the catalog.</summary>
    [Fact]
    public void The_built_ins_are_the_nineteen_names_of_the_design() =>
        CelFunctionCatalog.BuiltIns.Names.ShouldBe(
        [
            "contains", "endsWith", "int", "lowerAscii", "math.abs", "math.ceil", "math.floor", "math.greatest", "math.least",
            "math.round", "now", "replace", "size", "startsWith", "string", "substring", "timestamp", "trim", "upperAscii",
        ]);

    /// <summary>
    /// Spec §5's count: ten names with one overload (<c>now</c> among them), seven with two (<c>math.abs</c>,
    /// <c>math.ceil</c>, <c>math.floor</c>, <c>math.greatest</c>, <c>math.least</c>, <c>substring</c>, <c>int</c>),
    /// <c>math.round</c> with three and <c>string</c> with five: 10 + 14 + 3 + 5.
    /// </summary>
    [Fact]
    public void There_are_thirty_two_built_in_overloads() => CelFunctionCatalog.BuiltIns.Functions.Count.ShouldBe(32);

    [Fact]
    public void Only_the_digits_overload_declares_a_constant_check() =>
        CelFunctionCatalog.BuiltIns.Functions.Where(function => function.ConstantCheck is not null)
            .ShouldHaveSingleItem().Signature().ShouldBe("math.round(x: Decimal, digits: Int) -> Decimal");

    [Fact]
    public void Now_is_the_one_legacy_call_and_offered_in_mutate_only() =>
        CelFunctionCatalog.BuiltIns.Functions.Where(function => function.IsLegacy).ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(
                now => now.Name.ShouldBe(CelCall.Now),
                now => now.Profiles.SetEquals(new[] { CelProfile.Mutate }).ShouldBeTrue());

    [Fact]
    public void Every_built_in_but_now_is_offered_in_condition_and_mutate_only() =>
        CelFunctionCatalog.BuiltIns.Functions.Where(function => function.Name != CelCall.Now)
            .ShouldAllBe(function => function.Profiles.SetEquals(new[] { CelProfile.Condition, CelProfile.Mutate }));

    [Fact]
    public void Overloads_of_one_name_share_their_parameter_names_by_position() =>
        CelFunctionCatalog.BuiltIns.Functions.GroupBy(function => function.Name).ShouldAllBe(group =>
            group.All(overload => overload.Parameters.Select(p => p.Name)
                .SequenceEqual(group.OrderByDescending(o => o.Parameters.Count).First().Parameters.Take(overload.Parameters.Count).Select(p => p.Name))));

    /// <summary>
    /// A terminal period and a length only: a summary may hold a decimal point (<c>2.5 is 3</c>), so "one sentence" is
    /// not something a test can check (preflight F6-3).
    /// </summary>
    [Fact]
    public void Every_built_in_summary_ends_with_a_period_and_fits_two_hundred_characters() =>
        CelFunctionCatalog.BuiltIns.Functions.ShouldAllBe(function => function.Summary.EndsWith('.') && function.Summary.Length <= 200);

    [Fact]
    public void Describe_is_ordinal_by_name_with_overloads_in_declaration_order()
    {
        var catalog = CelFunctionCatalog.BuiltIns.With([Host("Zeta"), Host("alpha")]);

        var described = catalog.Describe();

        described.Select(f => f.Name).Distinct().ShouldBe([.. BuiltInNames.Append("Zeta").Append("alpha").Order(StringComparer.Ordinal)]);
        described.Select(f => f.Name).ShouldBeInOrder(SortDirection.Ascending, StringComparer.Ordinal);
        described.Count.ShouldBe(catalog.Functions.Count);
        described.Where(f => f.Name == "math.abs").Select(f => f.Result).ShouldBe([CelValueType.Int, CelValueType.Decimal]);
        described.Where(f => f.Name == "math.round").Select(f => f.Parameters.Count).ShouldBe([1, 1, 2]);
        described.Where(f => f.Name == "string").Select(f => f.Parameters[0].Type)
            .ShouldBe([CelValueType.Int, CelValueType.Decimal, CelValueType.Bool, CelValueType.Uuid, CelValueType.Timestamp]);
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
