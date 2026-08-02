using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Exercises <see cref="TestFieldSqlRenderer"/> and <see cref="SqlPredicate"/> directly, independent
/// of <see cref="MMLib.Alvo.Expressions.Internal.SqlPredicateRenderer"/>: the identifier-quoting
/// obligation <see cref="IFieldSqlRenderer.RenderField"/> now documents, the case-insensitive
/// <c>LIKE</c> composition, and the safe-default predicate factory.
/// </summary>
public class TestFieldSqlRendererTests
{
    private static readonly TestFieldSqlRenderer _fields = new();

    /// <summary>
    /// A field name is the one string <see cref="IFieldSqlRenderer.RenderField"/> receives
    /// unparameterized. A descriptor-sourced name is pattern-bound at the JSON Schema layer, but a
    /// host can build an <see cref="EntitySchema"/> programmatically with no such check — this pins
    /// that <see cref="TestFieldSqlRenderer"/> still produces a single, safe, well-formed quoted
    /// identifier for a hostile name, never a way to break out of the identifier.
    /// </summary>
    [Fact]
    public void RenderField_neutralizes_a_hostile_field_name_from_a_programmatically_built_schema()
    {
        var entity = new EntitySchema { Name = "orders", Fields = [new FieldSchema { Name = "x", Type = FieldType.Uuid }] };

        _fields.RenderField(entity, "ow\"ner_id").ShouldBe("\"ow\"\"ner_id\"");
        _fields.RenderField(entity, "owner_id'); DROP TABLE orders; --").ShouldBe("\"owner_id'); DROP TABLE orders; --\"");
    }

    [Fact]
    public void RenderCaseInsensitiveLike_composes_an_upper_cased_like()
    {
        _fields.RenderCaseInsensitiveLike("\"title\"", "@p0").ShouldBe("UPPER(\"title\") LIKE UPPER(@p0)");
    }

    [Fact]
    public void AlwaysFalse_renders_the_dialects_false_literal_with_no_parameters()
    {
        var predicate = SqlPredicate.AlwaysFalse(_fields);

        predicate.Sql.ShouldBe("FALSE");
        predicate.Parameters.ShouldBeEmpty();
    }
}
