using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The fields a mutate may name (spec §4.3): declared in the working copy, not managed, not derived.</summary>
public class HookFieldsTests
{
    private const string Working = """
        {
          "entities": {
            "orders": {
              "audit": true,
              "fields": {
                "status": { "type": "enum", "values": ["open", "closed"], "required": true },
                "total": { "type": "decimal", "computed": "price * quantity" },
                "lines_count": { "type": "integer", "rollup": { "op": "count", "from": "order_lines" } },
                "price": { "type": "decimal" },
                "quantity": { "type": "integer" }
              }
            }
          }
        }
        """;

    [Fact]
    public void Every_declared_field_is_known_by_name_with_its_type()
    {
        var declared = HookFields.Declared(Working, "orders");

        declared.Keys.ShouldBe(["status", "total", "lines_count", "price", "quantity"], ignoreOrder: true);
        declared["status"].EnumValues.ShouldBe(["open", "closed"]);
        declared["status"].Required.ShouldBeTrue();
    }

    [Fact]
    public void A_computed_or_rollup_field_is_not_writable()
        => HookFields.Writable(Working, "orders").ShouldBe(["status", "price", "quantity"]);

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public void An_entity_the_copy_does_not_declare_has_no_fields(string entity)
    {
        HookFields.Declared(Working, entity).ShouldBeEmpty();
        HookFields.Writable(Working, entity).ShouldBeEmpty();
    }

    [Fact]
    public void A_field_key_written_twice_is_offered_once()
        => HookFields.Writable("""{"entities":{"orders":{"fields":{"price":{"type":"decimal"},"price":{"type":"integer"}}}}}""", "orders")
            .ShouldBe(["price"]);

    /// <summary>
    /// The two readers take the same declaration of a doubled key — the first — so the form never offers a field whose
    /// facets <see cref="HookFields.Declared"/> reads from a different declaration than the one it was offered for.
    /// </summary>
    [Fact]
    public void A_field_key_written_twice_is_judged_by_its_first_declaration_in_both_readers()
    {
        const string doubled = """{"entities":{"orders":{"fields":{"price":{"type":"decimal","computed":"1"},"price":{"type":"integer"}}}}}""";

        HookFields.Declared(doubled, "orders")["price"].ComputedExpression.ShouldNotBeNull();
        HookFields.Writable(doubled, "orders").ShouldBeEmpty();
    }

    [Fact]
    public void Text_that_is_not_a_descriptor_has_no_fields()
        => HookFields.Writable("not json", "orders").ShouldBeEmpty();

    /// <summary>
    /// A type spelled as a number is unreadable, not an enum ordinal: <c>Enum.TryParse</c> alone turns <c>"42"</c> into a
    /// <see cref="FieldType"/> no member names, which <c>ConditionTable.KindOf</c> refuses by throwing.
    /// </summary>
    [Theory]
    [InlineData("42")]
    [InlineData("3")]
    [InlineData("-1")]
    public void A_type_written_as_a_number_is_read_as_unreadable_not_as_an_ordinal(string type)
    {
        var working = $$"""{ "entities": { "orders": { "fields": { "code": { "type": "{{type}}" } } } } }""";

        HookFields.Declared(working, "orders")["code"].Type.ShouldBe(FieldType.String);
    }
}
