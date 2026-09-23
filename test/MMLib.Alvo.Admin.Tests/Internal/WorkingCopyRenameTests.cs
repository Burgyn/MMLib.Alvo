using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Renaming an entity or a field, which is the one edit where the descriptor's name and the database's
/// name come apart.
/// </summary>
/// <remarks>
/// <b>Every fact here is about <c>renamedFrom</c> rather than about the key</b>, because moving the key is
/// the easy half and the half that loses data on its own: to the apply, a key that moved with no
/// <c>renamedFrom</c> is a drop and a create, and the rows go with the old table.
/// </remarks>
public class WorkingCopyRenameTests
{
    /// <summary>A rename moves the key and records where it came from.</summary>
    [Fact]
    public void Renaming_an_entity_carries_the_applied_name()
    {
        var copy = Copy();

        copy.RenameEntity("jobs", "work_orders").ShouldBeNull();

        Entity(copy, "work_orders").ShouldContain("\"renamedFrom\": \"jobs\"");
        copy.Entities.ShouldBe(["work_orders", "jobs_archive"]);
    }

    /// <summary>
    /// A second rename still names the applied table, not the intermediate.
    /// </summary>
    /// <remarks>
    /// The one an author cannot check by reading the file: after two renames the working copy says
    /// <c>orders</c>, the database still says <c>jobs</c>, and a <c>renamedFrom</c> naming <c>work_orders</c>
    /// would point the apply at a table that never existed.
    /// </remarks>
    [Fact]
    public void A_second_rename_still_names_the_applied_entity()
    {
        var copy = Copy();

        copy.RenameEntity("jobs", "work_orders").ShouldBeNull();
        copy.RenameEntity("work_orders", "orders").ShouldBeNull();

        Entity(copy, "orders").ShouldContain("\"renamedFrom\": \"jobs\"");
    }

    /// <summary>Renaming back to the applied name drops the key, because nothing moved.</summary>
    [Fact]
    public void Renaming_back_drops_the_rename()
    {
        var copy = Copy();

        copy.RenameEntity("jobs", "work_orders").ShouldBeNull();
        copy.RenameEntity("work_orders", "jobs").ShouldBeNull();

        Entity(copy, "jobs").ShouldNotContain("renamedFrom");
    }

    /// <summary>
    /// An entity this copy invented carries no rename at all.
    /// </summary>
    /// <remarks>
    /// There is no table behind it, so <c>renamedFrom</c> would name something the apply cannot find —
    /// which is a refusal at best and a drop of somebody else's table at worst.
    /// </remarks>
    [Fact]
    public void An_entity_that_was_never_applied_carries_no_rename()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: true, audited: false);

        copy.RenameEntity("invoices", "bills").ShouldBeNull();

        Entity(copy, "bills").ShouldNotContain("renamedFrom");
    }

    /// <summary>The renamed entity keeps its position, so a one-word change reads as one word.</summary>
    [Fact]
    public void A_rename_does_not_move_the_declaration()
    {
        var copy = Copy();

        copy.RenameEntity("jobs_archive", "archive").ShouldBeNull();

        copy.Entities.ShouldBe(["jobs", "archive"]);
    }

    /// <summary>A field rename carries the applied column name.</summary>
    [Fact]
    public void Renaming_a_field_carries_the_applied_name()
    {
        var copy = Copy();

        copy.RenameField("jobs", "title", "summary").ShouldBeNull();

        Field(copy, "jobs", "summary").ShouldContain("\"renamedFrom\": \"title\"");
    }

    /// <summary>
    /// A field on an entity that was itself renamed still resolves its applied column.
    /// </summary>
    /// <remarks>
    /// The case that catches a reader against the working copy rather than the applied one: after the
    /// entity moved, <c>_applied["entities"]["work_orders"]</c> is null, and a field rename that looked
    /// there would conclude the column is new and carry no <c>renamedFrom</c> — dropping it.
    /// </remarks>
    [Fact]
    public void A_field_on_a_renamed_entity_still_carries_its_own_rename()
    {
        var copy = Copy();

        copy.RenameEntity("jobs", "work_orders").ShouldBeNull();
        copy.RenameField("work_orders", "title", "summary").ShouldBeNull();

        Field(copy, "work_orders", "summary").ShouldContain("\"renamedFrom\": \"title\"");
    }

    /// <summary>A name the frozen schema cannot carry is refused, with the pattern.</summary>
    [Theory]
    [InlineData("Work_Orders")]
    [InlineData("9jobs")]
    [InlineData("work-orders")]
    [InlineData("")]
    public void A_name_the_schema_cannot_carry_is_refused(string to)
    {
        var copy = Copy();

        copy.RenameEntity("jobs", to).ShouldNotBeNull().ShouldContain(DescriptorNames.Member);
        copy.Entities.ShouldContain("jobs");
    }

    /// <summary>A name something else already has is refused rather than merged over it.</summary>
    [Fact]
    public void A_name_already_taken_is_refused()
    {
        var copy = Copy();

        copy.RenameEntity("jobs", "jobs_archive").ShouldNotBeNull().ShouldContain("already declares");
        copy.Entities.ShouldBe(["jobs", "jobs_archive"]);
    }

    /// <summary>Renaming something to the name it has is refused rather than silently doing nothing.</summary>
    [Fact]
    public void Renaming_to_the_same_name_is_refused()
        => Copy().RenameEntity("jobs", "jobs").ShouldNotBeNull().ShouldContain("already has");

    /// <summary>Renaming something that is not there says so rather than throwing into a render.</summary>
    [Fact]
    public void Renaming_something_that_is_not_there_is_refused()
    {
        var copy = Copy();

        copy.RenameEntity("invoices", "bills").ShouldNotBeNull().ShouldContain("no entity called invoices");
        copy.RenameField("jobs", "absent", "present").ShouldNotBeNull().ShouldContain("no field called absent");
    }

    private static string Entity(WorkingCopy copy, string name)
    {
        using var document = System.Text.Json.JsonDocument.Parse(copy.Json);
        return System.Text.Json.JsonSerializer.Serialize(
            document.RootElement.GetProperty("entities").GetProperty(name), _asWritten);
    }

    private static string Field(WorkingCopy copy, string entity, string name)
    {
        using var document = System.Text.Json.JsonDocument.Parse(copy.Json);
        return System.Text.Json.JsonSerializer.Serialize(
            document.RootElement.GetProperty("entities").GetProperty(entity)
                .GetProperty("fields").GetProperty(name), _asWritten);
    }

    private static readonly System.Text.Json.JsonSerializerOptions _asWritten = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "jobs": {
                  "fields": {
                    "title": { "type": "string" },
                    "status": { "type": "string" }
                  }
                },
                "jobs_archive": { "fields": { "title": { "type": "string" } } }
              }
            }
            """,
            revision: 3);

        return copy;
    }
}
