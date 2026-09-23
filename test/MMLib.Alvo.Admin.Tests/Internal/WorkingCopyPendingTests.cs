using MMLib.Alvo.Admin.Internal;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// What the shell counts as pending, and what a tab badges as staged.
/// </summary>
/// <remarks>
/// <b>A count an operator can map back to what they did.</b> The bar says "3 unapplied changes"; if the three
/// are not the three things the operator remembers doing, the number is noise. So the unit is the one a person
/// names — an entity, a top-level block — and each fact below pins one way of getting it wrong.
/// </remarks>
public class WorkingCopyPendingTests
{
    [Fact]
    public void A_fresh_copy_has_nothing_pending()
    {
        var copy = Copy();

        copy.PendingCount.ShouldBe(0);
        copy.FieldChangesOf("customers").ShouldAllBe(field => field.Change == StagedChange.None);
    }

    [Fact]
    public void Three_fields_staged_on_one_entity_are_one_change()
    {
        var copy = Copy();

        copy.AddField("customers", "phone", Facets());
        copy.AddField("customers", "city", Facets());
        copy.RemoveField("customers", "notes");

        copy.PendingCount.ShouldBe(1, "the operator changed one entity, whatever it took to do it");
    }

    [Fact]
    public void An_added_entity_a_removed_entity_and_a_changed_block_are_three()
    {
        var copy = Copy();

        copy.AddEntity("invoices", scoped: false, audited: true);
        copy.RemoveEntity("regions");
        copy.Replace(copy.Json.Replace("\"roles\": []", "\"roles\": [ \"dispatcher\" ]", StringComparison.Ordinal));

        copy.PendingCount.ShouldBe(3);
    }

    [Fact]
    public void A_rename_is_one_change_and_not_a_drop_and_a_create()
    {
        var copy = Copy();

        copy.RenameEntity("regions", "service_areas").ShouldBeNull();

        copy.PendingCount.ShouldBe(1);
    }

    [Fact]
    public void Undoing_the_only_edit_leaves_nothing_pending()
    {
        var copy = Copy();

        copy.AddField("customers", "phone", Facets());
        copy.RemoveField("customers", "phone");

        copy.IsDirty.ShouldBeFalse();
        copy.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void Fields_are_badged_new_changed_and_removed()
    {
        var copy = Copy();

        copy.AddField("customers", "phone", Facets());
        copy.AddField("customers", "email", Facets());
        copy.RemoveField("customers", "notes");

        copy.FieldChangesOf("customers").ShouldBe(
        [
            new StagedField("name", StagedChange.None),
            new StagedField("email", StagedChange.Changed),
            new StagedField("notes", StagedChange.Removed),
            new StagedField("phone", StagedChange.New),
        ]);
    }

    [Fact]
    public void A_removed_field_is_listed_where_it_was()
    {
        var copy = Copy();

        copy.RemoveField("customers", "email");

        copy.FieldChangesOf("customers").Select(field => field.Name).ShouldBe(["name", "email", "notes"]);
    }

    [Fact]
    public void A_renamed_field_is_one_changed_row_and_not_a_removed_one()
    {
        var copy = Copy();

        copy.RenameField("customers", "email", "contact_email").ShouldBeNull();

        copy.FieldChangesOf("customers").ShouldBe(
        [
            new StagedField("name", StagedChange.None),
            new StagedField("contact_email", StagedChange.Changed),
            new StagedField("notes", StagedChange.None),
        ]);
    }

    [Fact]
    public void Every_field_of_an_entity_the_copy_invented_is_new()
    {
        var copy = Copy();

        copy.AddEntity("invoices", scoped: false, audited: true);

        copy.FieldChangesOf("invoices").ShouldBe([new StagedField("name", StagedChange.New)]);
    }

    [Fact]
    public void A_restored_field_goes_back_where_it_was_and_the_copy_is_clean()
    {
        var copy = Copy();
        copy.RemoveField("customers", "email");

        copy.RestoreField("customers", "email");

        copy.IsDirty.ShouldBeFalse("a restore that moved the field would leave a diff nobody made");
    }

    [Fact]
    public void Only_the_index_the_copy_declared_is_staged()
    {
        var copy = Copy();

        copy.AddIndex("customers", ["name"], unique: false);

        copy.StagedIndexesOf("customers").ShouldBe([1]);
    }

    [Fact]
    public void A_second_copy_of_an_applied_hook_is_a_new_hook()
    {
        var copy = Copy();

        copy.AddHook("customers", "afterCreate", condition: null, Webhook());

        copy.StagedHooksOf("customers").ShouldBe([("afterCreate", 1)]);
    }

    [Fact]
    public void A_field_restored_beside_a_renamed_one_goes_back_after_the_rename()
    {
        var copy = new WorkingCopy();
        copy.Take("""{"entities":{"jobs":{"fields":{"a":{"type":"string"},"b":{"type":"string"},"c":{"type":"string"}}}}}""", 1);
        copy.RenameField("jobs", "a", "x").ShouldBeNull();
        copy.RemoveField("jobs", "b");

        copy.RestoreField("jobs", "b");

        copy.FieldsOf("jobs").Select(field => field.Key).ShouldBe(
            ["x", "b", "c"], "b followed a when it was applied, and x is a under a new name");
    }

    [Fact]
    public void A_remove_against_an_index_another_tab_moved_removes_nothing()
    {
        var copy = Copy();
        var drawn = copy.IndexesOf("customers")[0];

        copy.AddIndex("customers", ["name"], unique: false);
        copy.RemoveIndex("customers", 0);

        copy.RemoveIndex("customers", 0, drawn).ShouldBeFalse(
            "position 0 now holds the index on name, which is not the one the stale screen drew there");
        copy.IndexesOf("customers").Single().Fields.ShouldBe(["name"]);
    }

    [Fact]
    public void A_remove_against_the_hook_list_the_screen_drew_removes_it()
    {
        var copy = Copy();
        var drawn = copy.HooksOf("customers").Single().Value;

        copy.RemoveHook("customers", "afterCreate", 0, drawn).ShouldBeTrue();
    }

    [Fact]
    public void A_remove_against_a_hook_list_another_tab_changed_removes_nothing()
    {
        var copy = Copy();
        var drawn = copy.HooksOf("customers").Single().Value;

        copy.AddHook("customers", "afterCreate", "true", Webhook());

        copy.RemoveHook("customers", "afterCreate", 0, drawn).ShouldBeFalse();
        copy.StagedHooksOf("customers").ShouldBe([("afterCreate", 1)]);
    }

    [Fact]
    public void Every_edit_tells_whoever_shows_the_copy()
    {
        var copy = Copy();
        var raised = 0;
        copy.Changed += () => raised++;

        copy.AddField("customers", "phone", Facets());
        copy.RemoveField("customers", "phone");
        copy.RenameField("customers", "email", "contact_email");
        copy.RemoveField("customers", "notes");
        copy.RestoreField("customers", "notes");
        copy.Replace(copy.Json);
        copy.Discard();
        copy.Take(Descriptor, revision: 5);

        raised.ShouldBe(8);
    }

    [Fact]
    public void A_removal_that_removed_nothing_tells_nobody()
    {
        var copy = Copy();
        var raised = 0;
        copy.Changed += () => raised++;

        copy.RemoveField("customers", "no_such_field");
        copy.RemoveEntity("no_such_entity");

        raised.ShouldBe(0, "a redraw of every open tab for an edit that did not happen is noise");
    }

    [Fact]
    public void The_count_is_current_the_moment_the_event_is_raised()
    {
        var copy = Copy();
        var seen = -1;
        copy.Changed += () => seen = copy.PendingCount;

        copy.AddEntity("invoices", scoped: false, audited: true);

        seen.ShouldBe(1, "a handler redraws from the count, so it must be taken before the event");
    }

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 4);
        return copy;
    }

    private static JsonObject Facets() => new() { ["type"] = "string" };

    private static JsonObject Webhook() => new() { ["type"] = "webhook", ["endpoint"] = "dispatch" };

    private const string Descriptor = """
        {
          "name": "field-service",
          "auth": { "roles": [] },
          "entities": {
            "customers": {
              "fields": {
                "name": { "type": "string", "required": true },
                "email": { "type": "string", "format": "email" },
                "notes": { "type": "string" }
              },
              "indexes": [ { "fields": [ "email" ] } ],
              "hooks": { "afterCreate": [ { "action": { "type": "webhook", "endpoint": "dispatch" } } ] }
            },
            "regions": {
              "fields": { "code": { "type": "string" } }
            }
          }
        }
        """;
}
