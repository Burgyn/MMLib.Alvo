using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The runtime read model the data path queries through, asserted as metadata. Every facet this context
/// configures changes which columns a statement names or how a value round-trips, and none of it needs a
/// database to observe — a model outlives the context that built it and no connection is ever opened.
/// </summary>
public sealed class AlvoDataContextModelTests : IDisposable
{
    private const string Computed = "line_total";

    private readonly AlvoDataContext _context = NewContext(new SchemaModel([AlvoDataFixtures.Vehicle]));

    [Fact]
    public void An_entity_this_model_maps_has_a_row_set()
        => _context.Rows(AlvoDataFixtures.Vehicle.Name).ShouldNotBeNull();

    /// <summary>
    /// An entity absent from this model — undeclared or dynamic — is refused with one shared text rather than
    /// surfacing EF's own message naming the type, and which of the two it was must not be distinguishable
    /// from outside.
    /// </summary>
    /// <remarks>
    /// Scoped to those two deliberately. A caller DENIED BY A POLICY is a third case and is NOT covered by
    /// this claim: <c>EfAlvoData</c> throws <c>decision.DenyReason ?? UnknownEntityMessage</c>, so a denial
    /// carrying a reason is distinguishable on purpose — an authorized caller refused by a rule deserves to
    /// know why. An earlier wording here promised a three-way indistinguishability the source does not give.
    /// </remarks>
    [Fact]
    public void An_entity_this_model_does_not_map_is_refused_as_unauthorized()
    {
        // NOT `ShouldBe(AlvoDataContext.UnmappedEntityMessage)`: that compares the constant with itself, so it
        // holds however the constant is rewritten and pins nothing. What the doc comment above actually claims
        // is a NON-DISCLOSURE guarantee — the refusal must not tell the caller which entity it asked about,
        // because that is how an unauthorized probe enumerates a tenant's schema. Assert that instead.
        var refused = Should.Throw<AlvoAuthorizationException>(() => _context.Rows("no_such_entity"));

        refused.Message.ShouldNotContain("no_such_entity");
    }

    /// <summary>
    /// A dynamic entity is absent from this model and therefore refused exactly like an unknown one. F7
    /// serves it by registering a dialect, never by branching here.
    /// </summary>
    /// <remarks>
    /// Indistinguishability is a relation between two refusals, so it is asserted as one. Checking each
    /// refusal on its own — "throws", and separately "does not name what was asked for" — leaves the oracle
    /// open: give the dynamic branch its own wording and both single-sided assertions stay green while a
    /// caller can tell an entity that exists as dynamic from one that does not exist at all, by probing a
    /// name and diffing the message.
    /// </remarks>
    [Fact]
    public void A_dynamic_entity_is_refused_indistinguishably_from_an_unknown_one()
    {
        using var context = NewContext(new SchemaModel([Dynamic()]));

        var unknown = Should.Throw<AlvoAuthorizationException>(() => _context.Rows("no_such_entity"));
        var dynamicEntity = Should.Throw<AlvoAuthorizationException>(() => context.Rows("evidence"));

        dynamicEntity.Message.ShouldBe(unknown.Message);
        dynamicEntity.Message.ShouldNotContain("evidence");
    }

    /// <summary>
    /// Every property is optional whatever the schema says, because a <c>hidden</c> field is answered by
    /// projecting a typed SQL <c>NULL</c> in its place and a required property would make the shaper throw on
    /// that <c>NULL</c> — with a different exception type on each engine. Required-ness is enforced by the
    /// database's own <c>NOT NULL</c> on the write path instead.
    /// </summary>
    /// <param name="field">A field the schema declares required.</param>
    /// <remarks>
    /// The row key is excluded rather than overlooked: EF keeps a key property non-nullable whatever the
    /// builder asks for, and a <c>hidden</c> rule cannot mask it anyway.
    /// </remarks>
    [Theory]
    [InlineData("tenant_id")]
    [InlineData("plate")]
    public void Every_property_is_optional_even_where_the_schema_declares_it_required(string field)
    {
        AlvoDataFixtures.Vehicle.Fields.Single(candidate => candidate.Name == field).Required.ShouldBeTrue();

        Rows(AlvoDataFixtures.Vehicle).FindProperty(field)!.IsNullable.ShouldBeTrue();
    }

    /// <summary>
    /// A <c>computed</c> field's value comes from the store, so EF leaves the column out of the statement it
    /// writes. Without this every create on an entity carrying one would be refused by the engine
    /// (<c>cannot INSERT into generated column</c>) — including creates whose payload never mentioned it.
    /// </summary>
    [Fact]
    public void A_computed_field_takes_its_value_from_the_store()
        => Rows(ComputedVehicle()).FindProperty(Computed)!
            .ValueGenerated.ShouldBe(ValueGenerated.OnAddOrUpdate);

    /// <summary>
    /// The inverse, which is what makes the fact above about <c>computed</c> rather than about every
    /// property: an ordinary field is written by the statement, so nothing may mark it store-generated.
    /// </summary>
    [Fact]
    public void An_ordinary_field_is_written_by_the_statement()
        => Rows(ComputedVehicle()).FindProperty("plate")!
            .ValueGenerated.ShouldBe(ValueGenerated.Never);

    [Fact]
    public void A_declared_max_length_reaches_the_read_model()
        => Rows(AlvoDataFixtures.Vehicle).FindProperty("plate")!.GetMaxLength().ShouldBe(32);

    /// <summary>
    /// Precision and scale both reach the model: a <c>decimal</c> bound without them is a value the engine
    /// rounds on the way in, which is the wrong-stored-number failure class from the other direction.
    /// </summary>
    [Fact]
    public void A_declared_precision_and_scale_reach_the_read_model()
    {
        var price = Rows(AlvoDataFixtures.Vehicle).FindProperty("price")!;

        price.GetPrecision().ShouldBe(18);
        price.GetScale().ShouldBe(2);
    }

    /// <summary>
    /// The runtime model knows the entity's indexes even though it never creates one: it is what
    /// <c>ConstraintViolationTranslator</c> resolves the constraint name PostgreSQL reports against, and
    /// while this model declared none every unique violation there surfaced as a 500.
    /// </summary>
    [Fact]
    public void A_unique_index_is_declared_so_a_constraint_violation_can_be_resolved()
        => Rows(UniquePlateVehicle()).GetIndexes()
            .ShouldHaveSingleItem()
            .Properties.Select(property => property.Name).ShouldBe(["plate"]);

    [Fact]
    public void The_applied_schema_is_required()
        => Should.Throw<ArgumentNullException>(() => new AlvoDataContext(SqliteOptions(), null!, Guid.NewGuid()));

    public void Dispose() => _context.Dispose();

    private static IEntityType Rows(EntitySchema entity)
    {
        using var context = NewContext(new SchemaModel([entity]));
        return context.Model.FindEntityType(entity.Name)!;
    }

    /// <summary>The fixture entity with one <c>computed</c> field added — the only facet it does not carry.</summary>
    private static EntitySchema ComputedVehicle() => AlvoDataFixtures.Vehicle with
    {
        Fields = [
            .. AlvoDataFixtures.Vehicle.Fields,
            new FieldSchema { Name = Computed, Type = FieldType.Decimal, ComputedExpression = "mileage * price" },
        ],
    };

    /// <summary>The fixture entity with one <c>unique</c> field, and unscoped so the index spans it alone.</summary>
    private static EntitySchema UniquePlateVehicle() => new()
    {
        Name = "widget",
        Fields = [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "plate", Type = FieldType.String, Required = true, Unique = true },
        ],
    };

    private static EntitySchema Dynamic() => new()
    {
        Name = "evidence",
        Storage = EntityStorage.Dynamic,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    private static AlvoDataContext NewContext(SchemaModel schema)
        => new(SqliteOptions(), schema, Guid.NewGuid());

    private static DbContextOptions SqliteOptions()
    {
        var options = new DbContextOptionsBuilder();
        options.UseSqlite("Data Source=:memory:", static sqlite => sqlite.UseRelationalNulls());

        return options.Options;
    }
}
