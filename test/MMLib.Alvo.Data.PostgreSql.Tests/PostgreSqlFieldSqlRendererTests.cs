using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

/// <summary>
/// PostgreSQL's <c>IFieldSqlRenderer</c> has no golden CEL→SQL snapshot until the PostgreSQL snapshot
/// subclass lands, and no rule in that snapshot's table reaches
/// <c>RenderCaseInsensitiveLike</c> on either engine, so these are the only tests that see either.
/// </summary>
public class PostgreSqlFieldSqlRendererTests
{
    private static readonly PostgreSqlFieldSqlRenderer _fields = new();

    [Fact]
    public void The_boolean_literals_are_postgresqls_own()
    {
        _fields.TrueLiteral.ShouldBe("TRUE");
        _fields.FalseLiteral.ShouldBe("FALSE");
    }

    [Fact]
    public void A_field_renders_as_a_quoted_column()
        => _fields.RenderField(Entity(), "owner_id").ShouldBe("\"owner_id\"");

    [Fact]
    public void A_quote_breaking_field_name_cannot_escape_the_quoted_identifier()
        => _fields.RenderField(Entity(), "a\"; DROP TABLE vehicle; --")
            .ShouldBe("\"a\"\"; DROP TABLE vehicle; --\"");

    [Fact]
    public void A_missing_entity_is_refused()
        => Should.Throw<ArgumentNullException>(() => _fields.RenderField(null!, "owner_id"));

    [Fact]
    public void A_parameter_renders_with_the_at_sigil_and_nothing_else()
        => _fields.RenderParameter("alvo_u0").ShouldBe("@alvo_u0");

    /// <summary>Native <c>ILIKE</c>, not the upper-casing emulation SQLite needs.</summary>
    [Fact]
    public void A_case_insensitive_like_is_native_ilike()
        => _fields.RenderCaseInsensitiveLike("\"plate\"", "@alvo_f0").ShouldBe("\"plate\" ILIKE @alvo_f0");

    /// <summary>
    /// The three two-valued members must come from the port's default interface members — overriding them
    /// is only correct for a dialect with no boolean type (T-SQL). Asserted through the interface, which is
    /// both the only way to reach a default member and what makes this catch a shadowing declaration: an
    /// implicit re-implementation on the class would take over the interface dispatch too.
    /// </summary>
    [Fact]
    public void The_two_valued_members_keep_the_ports_defaults()
    {
        ((IFieldSqlRenderer)_fields).RenderTwoValued("\"owner_id\" = @alvo_u0")
            .ShouldBe("COALESCE(\"owner_id\" = @alvo_u0, FALSE)");
        ((IFieldSqlRenderer)_fields).RenderBooleanFieldAsPredicate("\"is_public\"")
            .ShouldBe("COALESCE(\"is_public\", FALSE)");
        ((IFieldSqlRenderer)_fields).RenderBooleanPredicate(true).ShouldBe("TRUE");
        ((IFieldSqlRenderer)_fields).RenderBooleanPredicate(false).ShouldBe("FALSE");
    }

    /// <summary>
    /// PostgreSQL has a real <c>numeric</c>, which orders numerically, so no operand needs repairing — and
    /// a cast here would cost the index for nothing. This is the port's default, asserted through the
    /// interface so an accidental override on the class is caught.
    /// </summary>
    [Theory]
    [InlineData(CelValueType.Decimal)]
    [InlineData(CelValueType.Int)]
    [InlineData(CelValueType.String)]
    [InlineData(CelValueType.Timestamp)]
    [InlineData(CelValueType.Uuid)]
    public void Every_operand_is_already_comparable_and_is_left_alone(CelValueType type)
        => ((IFieldSqlRenderer)_fields).RenderComparableOperands("\"price\"", "@alvo_f0", type)
            .ShouldBe(("\"price\"", "@alvo_f0"));

    private static EntitySchema Entity() => new()
    {
        Name = "vehicle",
        Fields = [new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true }],
    };
}
