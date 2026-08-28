using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Abstractions.Tests.Schema;

/// <summary>
/// What the applied schema's half of a rollup guarantees: a resolved foreign key, and a shape that survives
/// the <c>schema_json</c> round trip the applied schema is stored through.
/// </summary>
public class RollupSchemaTests
{
    /// <summary>
    /// <see cref="RollupSchema.Via"/> is <c>required</c>, which is the type's whole reason for existing beside
    /// the descriptor's own <c>Rollup</c>: the descriptor may omit <c>via</c>, the mapper resolves it once, and
    /// no layer below is permitted to re-derive it. A fact rather than a comment because "required" is the only
    /// thing that makes the resolution unskippable — a nullable property would let a caller construct a rollup
    /// with no foreign key and have the write path guess one, which is a number aggregated over the wrong rows.
    /// </summary>
    [Fact]
    public void A_rollup_cannot_be_built_without_the_foreign_key_it_follows() =>
        typeof(RollupSchema).GetProperty(nameof(RollupSchema.Via))!
            .GetCustomAttributes(inherit: false)
            .Select(attribute => attribute.GetType().Name)
            .ShouldContain("RequiredMemberAttribute");

    /// <summary>
    /// <see cref="RollupSchema.Field"/> is the one optional part, because <see cref="RollupOperation.Count"/>
    /// aggregates rows rather than values — the frozen schema's own conditional (<c>field</c> is required for
    /// every op except <c>count</c>).
    /// </summary>
    [Fact]
    public void A_count_rollup_carries_no_field()
    {
        var rollup = new RollupSchema { From = "invoice_items", Op = RollupOperation.Count, Via = "invoice" };

        rollup.Field.ShouldBeNull();
        rollup.Op.ShouldBe(RollupOperation.Count);
    }

    /// <summary>
    /// The applied schema is persisted as JSON and restored on the next boot, so a rollup that did not survive
    /// the round trip would be a column the framework silently stops maintaining after a restart — the same
    /// failure the feature was refused for. Asserted over a whole <see cref="SchemaModel"/> rather than the
    /// record alone, because it is the model that is stored.
    /// </summary>
    [Fact]
    public void A_rollup_survives_the_applied_schema_round_trip()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "invoices",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema
                    {
                        Name = "net_total",
                        Type = FieldType.Decimal,
                        Nullable = true,
                        Rollup = new RollupSchema
                        {
                            From = "invoice_items",
                            Op = RollupOperation.Sum,
                            Field = "line_total",
                            Via = "invoice",
                        },
                    },
                ],
            },
        ]);

        var restored = JsonSerializer.Deserialize<SchemaModel>(JsonSerializer.Serialize(model))!;

        restored.Entities[0].Fields[1].Rollup.ShouldBe(model.Entities[0].Fields[1].Rollup);
    }

    /// <summary>
    /// <see cref="RollupOperation"/> round-trips through its <b>ordinal</b>, like every other enum on the
    /// applied schema, so a member inserted anywhere but the end silently re-reads every stored schema as a
    /// different operation — a <c>sum</c> column that starts holding a <c>count</c>. Pinned by value rather
    /// than described in a comment.
    /// </summary>
    [Theory]
    [InlineData(RollupOperation.Sum, 0)]
    [InlineData(RollupOperation.Count, 1)]
    [InlineData(RollupOperation.Avg, 2)]
    [InlineData(RollupOperation.Min, 3)]
    [InlineData(RollupOperation.Max, 4)]
    public void Every_rollup_operation_keeps_the_ordinal_a_stored_schema_was_written_with(
        RollupOperation operation, int ordinal) => ((int)operation).ShouldBe(ordinal);
}
