using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescriptorFieldType = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// The computed/rollup/hook ladder as the apply path enforces it — one fact per rung that cannot be resolved,
/// because every one of them is otherwise a <b>stored number that looks like data</b> rather than a loud
/// failure.
/// </summary>
/// <remarks>
/// These refusals replaced the two <c>UnhonouredFeatures</c> entries #21 deleted, and that is the point: the
/// feature is honoured in general, so "declared and dropped" is no longer the failure to protect against.
/// What is left is a declaration that <em>cannot be honoured as written</em> — an unresolvable foreign key, an
/// aggregate over a column that does not exist, a filter nothing applies — and each of those produces a wrong
/// aggregate silently.
/// </remarks>
public class RollupLadderTests
{
    /// <summary>
    /// A field may be <c>computed</c> or a <c>rollup</c>, never both: a generated column is maintained by the
    /// engine, which refuses every write to it, and a rollup is maintained by Alvo, which writes it. One of the
    /// two declarations would be a lie, and nothing in the descriptor says which.
    /// </summary>
    [Fact]
    public void A_field_declaring_both_computed_and_rollup_is_refused()
    {
        var refused = Should.Throw<InvalidDataException>(() => Map(Invoicing(netTotal: new FieldDescriptor
        {
            Type = DescriptorFieldType.Decimal,
            Computed = "unit_price * amount",
            Rollup = Sum("invoice_items", "line_total"),
        })));

        refused.Message.ShouldContain("both 'computed' and 'rollup'");
        refused.Message.ShouldContain("gross_total", Case.Insensitive);
    }

    /// <summary>
    /// The design's ladder rule: a rollup aggregates the records of a child that <b>points back</b> at this
    /// entity. With no foreign key there is nothing to follow, so the column would simply never be maintained —
    /// and no write would ever look for it.
    /// </summary>
    [Fact]
    public void A_rollup_whose_from_entity_does_not_reference_this_one_is_refused()
    {
        var refused = Should.Throw<InvalidDataException>(() => Map(Invoicing(
            netTotal: Rolled(Sum("payments", "amount")),
            extraEntity: ("payments", Payments(referencesInvoice: false)))));

        refused.Message.ShouldContain("does not reference 'invoices'");
        refused.Message.ShouldContain("\"entity\": \"invoices\"");
    }

    /// <summary>
    /// The frozen schema's own <c>follows.follower</c>/<c>follows.followee</c> case. Two references and no
    /// <c>via</c> has no defensible default: either choice yields a plausible number over the wrong
    /// relationship, and declaration order is not a decision the author made.
    /// </summary>
    [Fact]
    public void A_rollup_over_a_child_with_two_refs_to_this_parent_is_refused_unless_via_names_one()
    {
        var refused = Should.Throw<InvalidDataException>(() => Map(Invoicing(
            netTotal: Rolled(Sum("transfers", "amount")),
            extraEntity: ("transfers", TwoRefsToInvoices()))));

        refused.Message.ShouldContain("'rollup.via'");
        refused.Message.ShouldContain("from_invoice");
        refused.Message.ShouldContain("to_invoice");
    }

    /// <summary>
    /// And the same declaration is accepted once <c>via</c> names one of them — so the refusal above is about
    /// the ambiguity rather than about the shape.
    /// </summary>
    [Fact]
    public void A_via_that_names_one_of_the_two_references_resolves_it()
    {
        var model = Map(Invoicing(
            netTotal: Rolled(Sum("transfers", "amount", via: "to_invoice")),
            extraEntity: ("transfers", TwoRefsToInvoices())));

        Rollup(model, "invoices", "net_total").Via.ShouldBe("to_invoice");
    }

    /// <summary>A <c>via</c> that is not a reference to this parent is the typo version of the same failure.</summary>
    [Fact]
    public void A_rollup_naming_a_via_that_is_not_a_reference_to_this_parent_is_refused()
    {
        var refused = Should.Throw<InvalidDataException>(() => Map(Invoicing(
            netTotal: Rolled(Sum("invoice_items", "line_total", via: "unit_price")))));

        refused.Message.ShouldContain("via 'unit_price'");
        refused.Message.ShouldContain("is not a reference");
    }

    /// <summary>
    /// A typo in the aggregated field name is refused at apply, which the frozen JSON Schema cannot do: it
    /// types <c>rollup.field</c> as an identifier, not as a field of the child. Left through, the recompute
    /// would name a column that does not exist and the <em>first child write</em> would fail with the engine
    /// naming something the author never typed.
    /// </summary>
    [Fact]
    public void A_rollup_aggregating_a_field_the_child_does_not_declare_is_refused()
    {
        var refused = Should.Throw<InvalidDataException>(() => Map(Invoicing(
            netTotal: Rolled(Sum("invoice_items", "line_totl")))));

        refused.Message.ShouldContain("invoice_items.line_totl");
        refused.Message.ShouldContain("line_total");
    }

    /// <summary>
    /// Only <c>count</c> aggregates records rather than values. The frozen schema already makes <c>field</c>
    /// conditionally required, so this is the guard an embedded host that never runs the JSON Schema still
    /// passes through.
    /// </summary>
    [Fact]
    public void A_rollup_op_other_than_count_without_a_field_is_refused()
    {
        var refused = Should.Throw<InvalidDataException>(() => Map(Invoicing(
            netTotal: Rolled(new Rollup { From = "invoice_items", Op = RollupOp.Sum }))));

        refused.Message.ShouldContain("with no 'field'");
        refused.Message.ShouldContain("\"op\": \"count\"");
    }

    /// <summary><c>count</c> needs none, and keeps none on the applied schema.</summary>
    [Fact]
    public void A_count_rollup_needs_no_field_and_carries_none()
    {
        var model = Map(Invoicing(netTotal: Rolled(new Rollup { From = "invoice_items", Op = RollupOp.Count })));

        var rollup = Rollup(model, "invoices", "net_total");
        rollup.Op.ShouldBe(RollupOperation.Count);
        rollup.Field.ShouldBeNull();
        rollup.Via.ShouldBe("invoice");
    }

    /// <summary>
    /// A <c>where</c> is refused rather than ignored: an ignored filter still maintains the aggregate, over
    /// every child record instead of the declared subset, so the parent holds a number that is wrong by an
    /// amount only the data knows.
    /// </summary>
    [Fact]
    public void A_rollup_where_filter_is_refused_rather_than_ignored()
    {
        var refused = Should.Throw<InvalidDataException>(() => Map(Invoicing(
            netTotal: Rolled(Sum("invoice_items", "line_total", where: "status == 'open'")))));

        refused.Message.ShouldContain("aggregates every record");
        refused.Message.ShouldContain("'where'");
    }

    /// <summary>
    /// The happy path, and the one that proves the rest are about the declaration rather than about rollups:
    /// the single reference is resolved without a <c>via</c>, and the CEL source of a <c>computed</c> field
    /// reaches the applied schema unrendered.
    /// </summary>
    [Fact]
    public void A_resolvable_ladder_maps_the_rollup_and_the_computed_source()
    {
        var model = Map(Invoicing(netTotal: Rolled(Sum("invoice_items", "line_total"))));

        Rollup(model, "invoices", "net_total").ShouldBe(new RollupSchema
        {
            From = "invoice_items",
            Op = RollupOperation.Sum,
            Field = "line_total",
            Via = "invoice",
        });
        Field(model, "invoice_items", "line_total").ComputedExpression.ShouldBe("unit_price * amount");
    }

    private static SchemaModel Map(AlvoDescriptor descriptor) => DescriptorToSchemaMapper.Map(descriptor);

    private static RollupSchema Rollup(SchemaModel model, string entity, string field) =>
        Field(model, entity, field).Rollup.ShouldNotBeNull();

    private static FieldSchema Field(SchemaModel model, string entity, string field) =>
        model.Entities.Single(candidate => candidate.Name == entity)
            .Fields.Single(candidate => candidate.Name == field);

    private static FieldDescriptor Rolled(Rollup rollup) =>
        new() { Type = DescriptorFieldType.Decimal, Rollup = rollup };

    private static Rollup Sum(string from, string field, string? via = null, string? where = null) =>
        new() { From = from, Op = RollupOp.Sum, Field = field, Via = via, Where = where };

    /// <summary>
    /// <c>baas-analyza:1358</c>'s invoice, minus whatever a fact is testing: <c>invoice_items.line_total</c> is
    /// computed, and the parent's <paramref name="netTotal"/> is whatever the fact declares.
    /// </summary>
    private static AlvoDescriptor Invoicing(
        FieldDescriptor netTotal, (string Name, EntityDescriptor Entity)? extraEntity = null)
    {
        var entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["invoices"] = new EntityDescriptor
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["number"] = new FieldDescriptor { Type = DescriptorFieldType.String, Required = true },
                    ["net_total"] = netTotal,
                },
            },
            ["invoice_items"] = new EntityDescriptor
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["invoice"] = new FieldDescriptor { Type = DescriptorFieldType.Ref, Entity = "invoices" },
                    ["unit_price"] = new FieldDescriptor { Type = DescriptorFieldType.Decimal },
                    ["amount"] = new FieldDescriptor { Type = DescriptorFieldType.Integer },
                    ["line_total"] = new FieldDescriptor
                    {
                        Type = DescriptorFieldType.Decimal,
                        Computed = "unit_price * amount",
                    },
                },
            },
        };

        if (extraEntity is { } extra)
        {
            entities[extra.Name] = extra.Entity;
        }

        return new AlvoDescriptor { ApiVersion = "alvo.dev/v1", Name = "rollup-ladder", Entities = entities };
    }

    private static EntityDescriptor Payments(bool referencesInvoice)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["amount"] = new FieldDescriptor { Type = DescriptorFieldType.Decimal },
        };

        if (referencesInvoice)
        {
            fields["invoice"] = new FieldDescriptor { Type = DescriptorFieldType.Ref, Entity = "invoices" };
        }

        return new EntityDescriptor { Fields = fields };
    }

    /// <summary>The frozen schema's own ambiguity case, spelled as a transfer between two invoices.</summary>
    private static EntityDescriptor TwoRefsToInvoices() => new()
    {
        Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["from_invoice"] = new FieldDescriptor { Type = DescriptorFieldType.Ref, Entity = "invoices" },
            ["to_invoice"] = new FieldDescriptor { Type = DescriptorFieldType.Ref, Entity = "invoices" },
            ["amount"] = new FieldDescriptor { Type = DescriptorFieldType.Decimal },
        },
    };
}
