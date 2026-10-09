using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the hook editor offers under a CEL box, and what Insert writes (spec §9).</summary>
public class FunctionOfferTests
{
    private static CelFunctionInfo Info(string name, CelValueType result, CelFunctionProvenance provenance, CelProfile[] profiles, params (string Name, CelValueType Type)[] parameters) => new()
    {
        Name = name,
        Parameters = [.. parameters.Select(p => new CelFunctionParameter { Name = p.Name, Type = p.Type, AcceptsNull = false })],
        Result = result,
        ResultMayBeNull = false,
        Summary = $"{name} does a thing.",
        Provenance = provenance,
        Profiles = profiles,
    };

    private static readonly CelProfile[] _both = [CelProfile.Condition, CelProfile.Mutate];

    private static readonly IReadOnlyList<CelFunctionInfo> _catalog =
    [
        Info("math.ceil", CelValueType.Int, CelFunctionProvenance.BuiltIn, _both, ("x", CelValueType.Int)),
        Info("math.ceil", CelValueType.Decimal, CelFunctionProvenance.BuiltIn, _both, ("x", CelValueType.Decimal)),
        Info("math.round", CelValueType.Int, CelFunctionProvenance.BuiltIn, _both, ("x", CelValueType.Int)),
        Info("math.round", CelValueType.Decimal, CelFunctionProvenance.BuiltIn, _both, ("x", CelValueType.Decimal)),
        Info("now", CelValueType.Timestamp, CelFunctionProvenance.BuiltIn, [CelProfile.Mutate]),
        Info("normalizeFrameNumber", CelValueType.String, CelFunctionProvenance.Host, _both, ("value", CelValueType.String)),
        Info("substring", CelValueType.String, CelFunctionProvenance.BuiltIn, _both, ("text", CelValueType.String), ("start", CelValueType.Int)),
        Info("substring", CelValueType.String, CelFunctionProvenance.BuiltIn, _both, ("text", CelValueType.String), ("start", CelValueType.Int), ("end", CelValueType.Int)),
        Info("tax", CelValueType.Decimal, CelFunctionProvenance.BuiltIn, _both, ("amount", CelValueType.Decimal)),
        Info("weekday", CelValueType.String, CelFunctionProvenance.BuiltIn, _both, ("day", CelValueType.Int)),
    ];

    private static OfferedFunction Offered(string name) => FunctionOffer.For(_catalog, CelProfile.Mutate).Single(f => f.Name == name);

    [Fact]
    public void A_profile_is_offered_its_functions_one_row_per_name_in_name_order()
    {
        FunctionOffer.For(_catalog, CelProfile.Mutate).Select(f => f.Name).ShouldBe(["math.ceil", "math.round", "normalizeFrameNumber", "now", "substring", "tax", "weekday"]);
        FunctionOffer.For(_catalog, CelProfile.Condition).Select(f => f.Name).ShouldBe(["math.ceil", "math.round", "normalizeFrameNumber", "substring", "tax", "weekday"]);
    }

    [Fact]
    public void A_row_lists_every_overload_and_says_whether_the_host_wrote_it()
    {
        var round = Offered("math.round");
        round.Signatures.ShouldBe(["math.round(x: Int) -> Int", "math.round(x: Decimal) -> Decimal"]);
        round.IsHost.ShouldBeFalse();
        Offered("normalizeFrameNumber").IsHost.ShouldBeTrue();
    }

    [Fact]
    public void The_template_has_every_parameter_of_the_longest_overload_and_selects_the_first()
    {
        var substring = Offered("substring");

        FunctionOffer.Template(substring, firstArgument: null).ShouldBe(new Insertion("substring(text, start, end)", 10, 4));
    }

    [Fact]
    public void A_first_argument_fills_the_first_place_and_the_next_placeholder_is_selected()
    {
        var substring = Offered("substring");

        FunctionOffer.Template(substring, "new.title").ShouldBe(new Insertion("substring(new.title, start, end)", 21, 5));
    }

    [Fact]
    public void A_complete_call_leaves_the_caret_after_it()
    {
        var normalize = Offered("normalizeFrameNumber");
        var now = Offered("now");

        FunctionOffer.Template(normalize, "new.frame_number").ShouldBe(new Insertion("normalizeFrameNumber(new.frame_number)", 38, 0));
        FunctionOffer.Template(now, null).ShouldBe(new Insertion("now()", 5, 0));
    }

    [Theory]
    [InlineData(FieldType.String, "beforeCreate", "new.code")]
    [InlineData(FieldType.Integer, "beforeUpdate", null)]
    [InlineData(FieldType.String, "beforeDelete", null)]
    public void The_first_argument_is_the_row_s_field_only_when_its_type_fits_and_the_point_has_new(FieldType type, string point, string? expected)
    {
        var normalize = Offered("normalizeFrameNumber");

        FunctionOffer.FirstArgument(normalize, point, new FieldSchema { Name = "code", Type = type }).ShouldBe(expected);
    }

    [Fact]
    public void No_field_chosen_gives_no_first_argument()
    {
        var normalize = Offered("normalizeFrameNumber");

        FunctionOffer.FirstArgument(normalize, "beforeCreate", field: null).ShouldBeNull();
    }

    [Fact]
    public void A_function_without_parameters_takes_no_first_argument()
    {
        var now = Offered("now");

        FunctionOffer.FirstArgument(now, "beforeCreate", new FieldSchema { Name = "at", Type = FieldType.DateTime }).ShouldBeNull();
    }

    [Fact]
    public void An_int_field_fills_a_decimal_parameter() =>
        FunctionOffer.FirstArgument(Offered("tax"), "beforeCreate", new FieldSchema { Name = "qty", Type = FieldType.Integer }).ShouldBe("new.qty");

    [Fact]
    public void A_decimal_field_does_not_fill_an_int_parameter() =>
        FunctionOffer.FirstArgument(Offered("weekday"), "beforeCreate", new FieldSchema { Name = "price", Type = FieldType.Decimal }).ShouldBeNull();

    /// <summary>
    /// Ruling W-D: the template spells the longest overload, <c>math.ceil(x: Int)</c> here, but the Decimal overload takes
    /// a Decimal field — so any overload's first parameter decides, not only the template's.
    /// </summary>
    [Fact]
    public void A_decimal_field_fills_a_function_whose_other_overload_takes_a_decimal()
    {
        var ceil = Offered("math.ceil");

        ceil.FirstParameterTypes.ShouldBe([CelValueType.Int, CelValueType.Decimal]);
        FunctionOffer.FirstArgument(ceil, "beforeCreate", new FieldSchema { Name = "price", Type = FieldType.Decimal }).ShouldBe("new.price");
    }

    [Fact]
    public void A_signature_marks_a_nullable_parameter_and_a_nullable_result()
    {
        var function = Info("coalesce", CelValueType.String, CelFunctionProvenance.BuiltIn, _both, ("value", CelValueType.String)) with
        {
            Parameters = [new CelFunctionParameter { Name = "value", Type = CelValueType.String, AcceptsNull = true }],
            ResultMayBeNull = true,
        };

        FunctionOffer.Signature(function).ShouldBe("coalesce(value: String?) -> String?");
    }

    [Theory]
    [InlineData("", 0, 0, "trim(text)", 5)]
    [InlineData(" > 3", 0, 0, "trim(text) > 3", 5)]
    [InlineData("x == ", 5, 5, "x == trim(text)", 10)]
    [InlineData("čaj OLD", 4, 7, "čaj trim(text)", 9)]
    [InlineData("abc", 9, 2, "abctrim(text)", 8)]
    public void Insert_replaces_the_selection_and_moves_the_placeholder_with_it(string text, int start, int end, string expected, int selectFrom)
    {
        var inserted = FunctionOffer.Insert(text, start, end, new Insertion("trim(text)", 5, 4));

        inserted.Text.ShouldBe(expected);
        inserted.SelectFrom.ShouldBe(selectFrom);
        inserted.SelectLength.ShouldBe(4);
    }
}
