using MMLib.Alvo.Admin.Components.Schema;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

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
    public void An_unloaded_copy_is_taken_once()
    {
        var copy = new WorkingCopy();

        copy.TakeIfUnloaded(Descriptor, revision: 4).ShouldBeTrue();

        copy.Loaded.ShouldBeTrue();
        copy.Revision.ShouldBe(4);
        copy.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void A_loaded_copy_is_never_taken_over_its_edits()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);
        var raised = 0;
        copy.Changed += () => raised++;

        copy.TakeIfUnloaded(Descriptor, revision: 5).ShouldBeFalse(
            "another tab took the copy and staged an edit first; taking it again would discard that edit");

        copy.Revision.ShouldBe(4);
        copy.PendingCount.ShouldBe(1);
        copy.Entities.ShouldContain("invoices");
        raised.ShouldBe(0);
    }

    [Fact]
    public void An_unedited_copy_follows_a_revision_somebody_else_appended()
    {
        var copy = Copy();
        var raised = 0;
        copy.Changed += () => raised++;

        copy.TakeIfUnedited(Descriptor, revision: 5).ShouldBeTrue();

        copy.Revision.ShouldBe(5);
        copy.PendingCount.ShouldBe(0);
        raised.ShouldBe(1);
    }

    [Fact]
    public void An_edited_copy_is_never_taken_over_its_edits_when_the_head_moves()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);
        var raised = 0;
        copy.Changed += () => raised++;

        copy.TakeIfUnedited(Descriptor, revision: 5).ShouldBeFalse(
            "another tab staged an edit before the follow; taking the head would discard it");

        copy.Revision.ShouldBe(4);
        copy.PendingCount.ShouldBe(1);
        copy.Entities.ShouldContain("invoices");
        raised.ShouldBe(0);
    }

    [Fact]
    public void A_copy_nobody_took_is_not_taken_by_a_follow()
    {
        var copy = new WorkingCopy();

        copy.TakeIfUnedited(Descriptor, revision: 5).ShouldBeFalse();

        copy.Loaded.ShouldBeFalse("the next screen to read it takes the head then");
    }

    /// <summary>
    /// A genuine race: an edit on one thread against a follow on another, many times over. Whichever wins, the edit is
    /// never lost — the follow either took the head first and the edit landed on it, or saw the edit and refused.
    /// </summary>
    /// <remarks>
    /// Injecting the edit exactly between the check and the take is impossible by construction: both run inside one
    /// <c>Edit</c> step under the gate, and the only callback the copy raises (<c>Changed</c>) is raised after the gate is
    /// released. So the test races the two calls instead; the check-then-take it replaced loses the edit within a few
    /// hundred rounds.
    /// </remarks>
    [Fact]
    public async Task An_edit_racing_a_follow_is_never_lost()
    {
        var cancel = TestContext.Current.CancellationToken;
        for (var round = 0; round < 2000; round++)
        {
            var copy = Copy();
            using var start = new Barrier(2);
            var edit = Task.Run(() =>
            {
                start.SignalAndWait();
                copy.AddEntity("invoices", scoped: false, audited: true);
            }, cancel);
            var follow = Task.Run(() =>
            {
                start.SignalAndWait();
                copy.TakeIfUnedited(Descriptor, revision: 5);
            }, cancel);

            await Task.WhenAll(edit, follow);

            copy.Entities.ShouldContain("invoices", $"round {round}: the follow discarded an edit");
        }
    }

    [Fact]
    public void An_applied_copy_starts_again_from_the_revision_it_wrote()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);
        var sent = copy.Json;

        copy.TakeApplied(sent, applied: 5, head: sent, headRevision: 5).ShouldBe(WorkingCopy.AppliedFollow.Restarted);

        copy.Revision.ShouldBe(5);
        copy.PendingCount.ShouldBe(0);
        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void An_applied_copy_starts_again_from_a_head_somebody_moved_since()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);
        var sent = copy.Json;
        var later = Later(sent);

        copy.TakeApplied(sent, applied: 5, head: later, headRevision: 6).ShouldBe(WorkingCopy.AppliedFollow.Restarted);

        copy.Revision.ShouldBe(6, "the copy held nothing but what was sent, so it has nothing to lose to the newer head");
        copy.IsDirty.ShouldBeFalse();
        copy.Entities.ShouldContain("later");
    }

    [Fact]
    public void A_discard_while_the_apply_was_on_the_wire_starts_again_rather_than_staging_a_revert()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);
        var sent = copy.Json;
        copy.Discard();

        copy.TakeApplied(sent, applied: 5, head: sent, headRevision: 5).ShouldBe(WorkingCopy.AppliedFollow.Restarted);

        copy.Revision.ShouldBe(5);
        copy.IsDirty.ShouldBeFalse("a discarded copy holds no edits, so there is nothing to stage over the new revision");
        copy.Entities.ShouldContain("invoices");
    }

    [Fact]
    public void An_edit_staged_while_the_apply_was_on_the_wire_stays_staged_over_the_new_revision()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);
        var sent = copy.Json;
        copy.AddEntity("tickets", scoped: false, audited: true);

        copy.TakeApplied(sent, applied: 5, head: sent, headRevision: 5).ShouldBe(
            WorkingCopy.AppliedFollow.Rebased, "another tab staged tickets after the send; restarting would discard it");

        copy.Revision.ShouldBe(5, "everything else in the copy is what revision 5 holds");
        copy.Entities.ShouldContain("tickets");
        copy.PendingCount.ShouldBe(1, "only tickets is left to apply");
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(5, false)]
    public void A_late_edit_over_a_head_that_is_not_the_revision_just_written_keeps_the_old_base(int headRevision, bool sameDocument)
    {
        var copy = Copy();
        var before = copy.AppliedJson;
        copy.AddEntity("invoices", scoped: false, audited: true);
        var sent = copy.Json;
        copy.AddEntity("tickets", scoped: false, audited: true);
        var head = sameDocument ? sent : Later(sent);

        copy.TakeApplied(sent, applied: 5, head, headRevision).ShouldBe(
            WorkingCopy.AppliedFollow.Kept,
            "rebasing onto somebody else's revision would stage a silent revert of it and pass If-Match");

        copy.Revision.ShouldBe(4, "the next apply is refused as a conflict, which is the honest answer");
        copy.AppliedJson.ShouldBe(before);
        copy.Entities.ShouldContain("tickets");
    }

    /// <summary>
    /// A fresh take forgets what the previous copy's apply was meant to say: the suggestion described edits that are
    /// gone, and Preview would pre-fill a reason for a change nobody staged.
    /// </summary>
    [Fact]
    public void A_fresh_take_forgets_the_suggested_reason()
    {
        var copy = Copy();
        copy.SuggestReason("Add invoices");

        copy.Take(Descriptor, revision: 5);

        copy.SuggestedReason.ShouldBeNull();
    }

    /// <summary>An apply that starts the copy again leaves no suggestion behind for the next one.</summary>
    [Fact]
    public void An_apply_that_restarts_the_copy_forgets_the_suggested_reason()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);
        copy.SuggestReason("Add invoices");
        var sent = copy.Json;

        copy.TakeApplied(sent, applied: 5, head: sent, headRevision: 5).ShouldBe(WorkingCopy.AppliedFollow.Restarted);

        copy.SuggestedReason.ShouldBeNull("the edits it described are revision 5 now");
    }

    /// <summary>The document and the revision it was taken from are read as one pair, the pair an apply sends.</summary>
    [Fact]
    public void A_snapshot_is_the_working_document_with_the_revision_it_is_against()
    {
        var copy = Copy();
        copy.AddEntity("invoices", scoped: false, audited: true);

        var snapshot = copy.Snapshot();

        snapshot.Json.ShouldBe(copy.Json);
        snapshot.Revision.ShouldBe(4);
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

    /// <summary><paramref name="descriptor"/> with one more entity: a revision somebody else applied on top of it.</summary>
    private static string Later(string descriptor)
    {
        var later = JsonNode.Parse(descriptor)!.AsObject();
        later["entities"]!.AsObject()["later"] = new JsonObject { ["fields"] = new JsonObject { ["code"] = Facets() } };
        return later.ToJsonString();
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
