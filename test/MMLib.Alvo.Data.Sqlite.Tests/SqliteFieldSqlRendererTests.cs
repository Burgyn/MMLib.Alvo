using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The golden CEL→SQL snapshot covers the members a rule reaches; no rule in its table reaches
/// <c>RenderCaseInsensitiveLike</c>, and a snapshot cannot express an argument guard, so those live here.
/// </summary>
public class SqliteFieldSqlRendererTests
{
    private static readonly SqliteFieldSqlRenderer _fields = new();

    [Fact]
    public void The_boolean_literals_are_sqlites_integers()
    {
        _fields.TrueLiteral.ShouldBe("1");
        _fields.FalseLiteral.ShouldBe("0");
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

    /// <summary>
    /// SQLite's <c>LIKE</c> is case-insensitive for ASCII only and case-<em>sensitive</em> for everything
    /// else, so the emulation upper-cases both operands rather than relying on the operator.
    /// </summary>
    [Fact]
    public void A_case_insensitive_like_upper_cases_both_operands()
        => _fields.RenderCaseInsensitiveLike("\"plate\"", "@alvo_f0")
            .ShouldBe("UPPER(\"plate\") LIKE UPPER(@alvo_f0)");

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
            .ShouldBe("COALESCE(\"owner_id\" = @alvo_u0, 0)");
        ((IFieldSqlRenderer)_fields).RenderBooleanFieldAsPredicate("\"is_public\"")
            .ShouldBe("COALESCE(\"is_public\", 0)");
        ((IFieldSqlRenderer)_fields).RenderBooleanPredicate(true).ShouldBe("1");
        ((IFieldSqlRenderer)_fields).RenderBooleanPredicate(false).ShouldBe("0");
    }

    private static EntitySchema Entity() => new()
    {
        Name = "vehicle",
        Fields = [new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true }],
    };
}
