using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The type rules the checker resolves rather than refuses: what arithmetic promotes to, and where
/// the enum declared-value check does and does not apply. A computed column is created with the
/// type this resolves to, so "which type came out" is as load-bearing as "was it accepted".
/// </summary>
public class CelTypeCheckerTypeRuleTests
{
    /// <summary>
    /// Arithmetic is <see cref="CelValueType.Decimal"/> when <em>either</em> operand is, and
    /// <see cref="CelValueType.Int"/> only when neither is — so an integer expression is not widened
    /// for nothing, and a decimal one never narrows to integer on the side it was written.
    /// </summary>
    [Theory]
    [InlineData("1 + 1", CelValueType.Int)]
    [InlineData("total + 1", CelValueType.Decimal)]
    [InlineData("1 + total", CelValueType.Decimal)]
    [InlineData("total + total", CelValueType.Decimal)]
    public void Arithmetic_is_decimal_when_either_operand_is_and_integer_only_when_neither_is(
        string source, CelValueType expected)
    {
        CelFixtures.CompileComputed(source).ResultType.ShouldBe(expected);
    }

    /// <summary>
    /// The declared-value check is an equality rule. A relational comparison against an undeclared
    /// spelling is not an enum-membership mistake — it is an ordering over the column's text, which
    /// <see cref="CelProfile.Computed"/> allows and the database alone evaluates.
    /// </summary>
    [Fact]
    public void The_declared_value_check_does_not_reach_a_relational_comparison()
    {
        CelFixtures.Compiler.Compile("status < 'aproved' ? 1 : 2", CelProfile.Computed, CelFixtures.Orders)
            .IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// And it keys off the field's declared <see cref="FieldType.Enum"/> type, not off a value list
    /// that happens to hang on a field of some other type — a string column is a string column, and
    /// a stray list does not quietly turn it into a closed set.
    /// </summary>
    [Fact]
    public void The_declared_value_check_keys_off_the_enum_field_type_and_not_a_stray_value_list()
    {
        var entity = new EntitySchema
        {
            Name = "labels",
            Fields = [new FieldSchema { Name = "label", Type = FieldType.String, EnumValues = ["draft"] }],
        };

        CelFixtures.Compiler.Compile("label == 'anything'", CelProfile.Rule, entity).IsSuccess.ShouldBeTrue();
    }
}
