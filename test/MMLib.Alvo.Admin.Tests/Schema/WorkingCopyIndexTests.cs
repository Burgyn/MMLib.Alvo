using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// What declaring an index does to the working document.
/// </summary>
/// <remarks>
/// <b>Asserted against the JSON rather than against the projection.</b> The descriptor is the artifact an
/// operator commits and diffs, and design §6.3's third criterion is that <c>GET descriptor</c> equals what
/// the editor sent — so a fact that read the shape back through <see cref="WorkingCopy.IndexesOf"/> alone
/// would pass for a document nobody could apply.
/// </remarks>
public class WorkingCopyIndexTests
{
    /// <summary>The frozen shape, and only it: <c>fields</c> in order, <c>unique</c> when it is true.</summary>
    [Fact]
    public void An_index_is_written_in_the_schemas_own_shape()
    {
        var copy = Copy();

        copy.AddIndex("work_orders", ["status", "scheduled_for"], unique: false);

        copy.Json.ShouldContain("\"indexes\"");
        Declared(copy).ShouldBe("""{"fields":["status","scheduled_for"]}""");
    }

    /// <summary>
    /// Uniqueness is written only when it is asked for.
    /// </summary>
    /// <remarks>
    /// <c>unique: false</c> is the schema's own default, and a descriptor full of restated defaults is one
    /// whose diffs stop saying what changed — the argument the field editor's booleans already make.
    /// </remarks>
    [Fact]
    public void Uniqueness_is_written_only_when_it_is_asked_for()
    {
        var copy = Copy();

        copy.AddIndex("work_orders", ["code"], unique: true);

        Declared(copy).ShouldBe("""{"fields":["code"],"unique":true}""");
    }

    /// <summary>
    /// The field order is the one that was picked, because it is what the index is for.
    /// </summary>
    /// <remarks>
    /// An index on <c>(a, b)</c> serves a filter on <c>a</c> and does nothing for one on <c>b</c> alone, so
    /// a control that sorted or de-duplicated the names would quietly declare a different index from the one
    /// the operator asked for — and the database would agree with the descriptor, not with them.
    /// </remarks>
    [Fact]
    public void The_field_order_is_the_one_that_was_picked()
    {
        var copy = Copy();

        copy.AddIndex("work_orders", ["scheduled_for", "status"], unique: false);

        Declared(copy).ShouldBe("""{"fields":["scheduled_for","status"]}""");
    }

    /// <summary>A second index joins the first rather than replacing it.</summary>
    [Fact]
    public void A_second_index_joins_the_first()
    {
        var copy = Copy();

        copy.AddIndex("work_orders", ["status"], unique: false);
        copy.AddIndex("work_orders", ["scheduled_for"], unique: true);

        var declared = copy.IndexesOf("work_orders");
        declared.Count.ShouldBe(2);
        declared[0].Fields.ShouldBe(["status"]);
        declared[1].Unique.ShouldBeTrue();
    }

    /// <summary>
    /// Indexes an author declared that this editor cannot draw survive an edit beside them.
    /// </summary>
    /// <remarks>
    /// The silent-narrowing case, one level down from the one the field editor already refuses: a control
    /// that rebuilt the array from what it can render would drop whatever it does not understand, and an
    /// operator who added one index would lose another without being told.
    /// </remarks>
    [Fact]
    public void An_index_the_editor_did_not_write_survives_beside_it()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" } },
                  "indexes": [ { "fields": ["status"], "x-owner": "the-migration" } ]
                }
              }
            }
            """,
            revision: 3);

        copy.AddIndex("work_orders", ["scheduled_for"], unique: false);

        copy.Json.ShouldContain("x-owner");
    }

    /// <summary>Removing takes the one at that position, and leaves the rest where they were.</summary>
    [Fact]
    public void Removing_takes_the_index_at_that_position()
    {
        var copy = Copy();
        copy.AddIndex("work_orders", ["status"], unique: false);
        copy.AddIndex("work_orders", ["scheduled_for"], unique: false);
        copy.AddIndex("work_orders", ["code"], unique: false);

        copy.RemoveIndex("work_orders", 1);

        copy.IndexesOf("work_orders").Select(index => index.Fields[0])
            .ShouldBe(["status", "code"]);
    }

    /// <summary>
    /// Removing the last one drops the block rather than leaving an empty array.
    /// </summary>
    /// <remarks>
    /// <c>"indexes": []</c> and no <c>indexes</c> are the same instruction and different documents, and the
    /// second is the one an author would have written. <c>SetRule</c> already drops an emptied <c>rules</c>
    /// for the same reason.
    /// </remarks>
    [Fact]
    public void Removing_the_last_index_drops_the_block()
    {
        var copy = Copy();
        copy.AddIndex("work_orders", ["status"], unique: false);

        copy.RemoveIndex("work_orders", 0);

        copy.Json.ShouldNotContain("indexes");
        copy.IndexesOf("work_orders").ShouldBeEmpty();
    }

    /// <summary>
    /// A position nothing is at changes nothing, rather than throwing into a render.
    /// </summary>
    /// <remarks>
    /// The copy is per-circuit, so this is not a race — it is the ordinary defensive shape the rest of
    /// <see cref="WorkingCopy"/> has, where an edit against something that is not there is a no-op and never
    /// an exception a component has to catch.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void A_position_nothing_is_at_changes_nothing(int position)
    {
        var copy = Copy();
        copy.AddIndex("work_orders", ["status"], unique: false);
        var before = copy.Json;

        copy.RemoveIndex("work_orders", position);

        copy.Json.ShouldBe(before);
    }

    /// <summary>
    /// An index whose value is the wrong type renders as nothing rather than taking the page down.
    /// </summary>
    /// <remarks>
    /// <b>Reachable, not hypothetical.</b> <see cref="WorkingCopy.Replace"/> validates nothing beyond "is it
    /// JSON" on purpose — the apply is the authority — so a hand-edited import or an assistant's proposal can
    /// put <c>"unique": "yes"</c> in the working copy. <c>JsonNode.GetValue&lt;T&gt;</c> throws
    /// <see cref="InvalidOperationException"/> for that, and the entity page reads the indexes on every tab,
    /// so one bad entry would end the circuit rather than spoil one panel. <c>HooksTab</c> already degrades
    /// this way and this path did not.
    /// </remarks>
    [Theory]
    [InlineData("""{ "fields": [7] }""")]
    [InlineData("""{ "fields": "status" }""")]
    [InlineData("""{ "unique": true }""")]
    [InlineData("""["status"]""")]
    public void An_index_of_the_wrong_shape_renders_as_nothing(string declared)
    {
        var copy = new WorkingCopy();
        copy.Take(
            $$"""
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" } },
                  "indexes": [ {{declared}} ]
                }
              }
            }
            """,
            revision: 3);

        var read = Should.NotThrow(() => copy.IndexesOf("work_orders"));

        /* Kept, not skipped: RemoveIndex addresses the array by position, so dropping an unreadable entry
           here would shift every position after it and remove the wrong index. */
        read.ShouldHaveSingleItem().Fields.ShouldBeEmpty();
    }

    /// <summary>
    /// A readable index with an unreadable <c>unique</c> is still the index it names.
    /// </summary>
    /// <remarks>
    /// The line between this and the case above, and it is worth drawing: <c>"unique": "yes"</c> spoils one
    /// facet, not the declaration. Reading the whole entry as unreadable would hide an index the operator
    /// can see in their own file, and telling them it enforces uniqueness because a string is truthy
    /// somewhere would be worse. It falls back to the schema's own default, and the apply is what refuses
    /// the string.
    /// </remarks>
    [Fact]
    public void An_unreadable_unique_falls_back_to_the_schemas_default()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" } },
                  "indexes": [ { "fields": ["status"], "unique": "yes" } ]
                }
              }
            }
            """,
            revision: 3);

        var read = copy.IndexesOf("work_orders").ShouldHaveSingleItem();
        read.Fields.ShouldBe(["status"]);
        read.Unique.ShouldBeFalse();
    }

    /// <summary>
    /// A bad entry keeps its position, so removing the good one beside it removes the good one.
    /// </summary>
    /// <remarks>
    /// This is why an unreadable index is rendered rather than filtered out. A screen that hid it would hand
    /// <see cref="WorkingCopy.RemoveIndex"/> a position one short of the truth, and the operator asking to
    /// drop <c>(status)</c> would silently drop something else.
    /// </remarks>
    [Fact]
    public void An_unreadable_index_keeps_the_positions_of_the_ones_beside_it()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" } },
                  "indexes": [ { "fields": [7] }, { "fields": ["status"] } ]
                }
              }
            }
            """,
            revision: 3);

        copy.IndexesOf("work_orders")[1].Fields.ShouldBe(["status"]);

        copy.RemoveIndex("work_orders", 1);

        copy.IndexesOf("work_orders").ShouldHaveSingleItem().Fields.ShouldBeEmpty();
    }

    /// <summary>An entity the document does not declare is not created by indexing it.</summary>
    [Fact]
    public void An_entity_that_is_not_declared_gains_nothing()
    {
        var copy = Copy();

        copy.AddIndex("invoices", ["status"], unique: false);

        copy.Json.ShouldNotContain("invoices");
        copy.IndexesOf("invoices").ShouldBeEmpty();
    }

    /// <summary>
    /// The relaxed encoder <see cref="WorkingCopy"/> itself writes with.
    /// </summary>
    /// <remarks>
    /// Without it these facts assert against a re-escaping of the document rather than the document: the
    /// default encoder turns every apostrophe into <c>\u0027</c>, and a CEL condition is mostly apostrophes.
    /// The expectation would then be pinning this helper's serializer, not what an operator commits.
    /// </remarks>
    private static readonly System.Text.Json.JsonSerializerOptions _asWritten = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The first declared index, as one line of JSON, for a fact that is about the document.
    /// </summary>
    /// <remarks>
    /// The whole entry rather than the keys it was asked about, so a key nobody asked for — a restated
    /// default, a stray <c>name</c> — fails the fact instead of passing beside it.
    /// </remarks>
    private static string Declared(WorkingCopy copy)
    {
        using var document = System.Text.Json.JsonDocument.Parse(copy.Json);
        var indexes = document.RootElement
            .GetProperty("entities").GetProperty("work_orders").GetProperty("indexes");

        return System.Text.Json.JsonSerializer.Serialize(indexes[0], _asWritten);
    }

    /// <summary>A copy over the smallest descriptor these facts need.</summary>
    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": {
                    "status": { "type": "string" },
                    "scheduled_for": { "type": "datetime" },
                    "code": { "type": "string" }
                  }
                }
              }
            }
            """,
            revision: 3);

        return copy;
    }
}
