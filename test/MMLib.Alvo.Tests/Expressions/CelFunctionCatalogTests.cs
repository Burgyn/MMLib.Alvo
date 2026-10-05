using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The catalog: name to overloads, built-ins first-class, host functions added once.</summary>
public sealed class CelFunctionCatalogTests
{
    private static CelFunction Host(string name) => new(
        name, [CelBuiltInFunctions.Parameter("s", CelValueType.String)], CelValueType.String, ResultNullable: true,
        "A host function.", IsHost: true, CelBuiltInFunctions.ConditionAndMutate, arguments => arguments[0]);

    [Fact]
    public void The_built_ins_are_the_two_legacy_calls_with_their_own_grammar()
    {
        var catalog = CelFunctionCatalog.BuiltIns;

        catalog.Names.ShouldBe(["lowerAscii", "now"]);
        catalog.Functions.ShouldAllBe(function => function.IsLegacy && !function.IsHost);
        catalog.Functions.ShouldAllBe(function => function.Profiles.SetEquals(new[] { CelProfile.Mutate }));
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

        catalog.Names.ShouldBe(["lowerAscii", "normalizePhone", "now"]);
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

    [Fact]
    public void A_signature_names_every_parameter_its_type_and_nullability()
    {
        CelFunctionCatalog.BuiltIns.Overloads("lowerAscii").Single().Signature().ShouldBe("lowerAscii(value: String) -> String?");
        CelFunctionCatalog.BuiltIns.Overloads("now").Single().Signature().ShouldBe("now() -> Timestamp");
    }
}
