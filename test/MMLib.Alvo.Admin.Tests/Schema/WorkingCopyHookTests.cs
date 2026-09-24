using MMLib.Alvo.Admin.Components.Schema;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// What declaring a hook does to the working document.
/// </summary>
/// <remarks>
/// Asserted against the JSON for <see cref="WorkingCopyIndexTests"/>' reason: the descriptor is the artifact
/// that is committed and applied, and a fact that read the shape back through the same projection that wrote
/// it would pass for a document nobody could apply.
/// </remarks>
public class WorkingCopyHookTests
{
    /// <summary>A guarded hook carries its condition first, then what it does.</summary>
    [Fact]
    public void A_hook_is_written_condition_first()
    {
        var copy = Copy();

        copy.AddHook(
            "work_orders", "beforeUpdate", "old.status == 'completed'", Reject("Cannot reopen."));

        Declared(copy, "beforeUpdate")
            .ShouldBe("""{"condition":"old.status == 'completed'","action":{"reject":"Cannot reopen."}}""");
    }

    /// <summary>
    /// A hook with no condition carries no <c>condition</c> key.
    /// </summary>
    /// <remarks>
    /// <c>"condition": ""</c> is not "always": it is an empty CEL expression, which the apply refuses. The
    /// absent key is what the schema means by a hook that always runs.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_hook_with_no_condition_carries_no_condition_key(string? condition)
    {
        var copy = Copy();

        copy.AddHook("work_orders", "afterCreate", condition, Webhook("dispatch"));

        Declared(copy, "afterCreate")
            .ShouldBe("""{"action":{"type":"webhook","endpoint":"dispatch"}}""");
    }

    /// <summary>A second hook at the same point joins the first, in order.</summary>
    /// <remarks>
    /// The order is the order they run in, so appending rather than replacing is the behaviour and not an
    /// implementation detail.
    /// </remarks>
    [Fact]
    public void A_second_hook_at_the_same_point_runs_after_the_first()
    {
        var copy = Copy();

        copy.AddHook("work_orders", "afterCreate", condition: null, Webhook("dispatch"));
        copy.AddHook("work_orders", "afterCreate", condition: null, Webhook("billing"));

        Declared(copy, "afterCreate").ShouldContain("dispatch");
        Declared(copy, "afterCreate", at: 1).ShouldContain("billing");
    }

    /// <summary>Two points on one entity are two lists, not one.</summary>
    [Fact]
    public void Two_points_are_two_lists()
    {
        var copy = Copy();

        copy.AddHook("work_orders", "beforeDelete", condition: null, Reject("No."));
        copy.AddHook("work_orders", "afterDelete", condition: null, Webhook("dispatch"));

        Declared(copy, "beforeDelete").ShouldContain("reject");
        Declared(copy, "afterDelete").ShouldContain("webhook");
    }

    /// <summary>
    /// A hook an author declared that this editor cannot draw survives an edit beside it.
    /// </summary>
    /// <remarks>
    /// The silent-narrowing case again, and the one worth measuring here: the editor writes four action
    /// shapes and the schema declares five, so a writer that rebuilt the list would drop an
    /// <c>entity.update</c> the moment somebody added a reject next to it.
    /// </remarks>
    [Fact]
    public void A_hook_the_editor_cannot_draw_survives_beside_it()
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
                  "hooks": {
                    "afterUpdate": [
                      { "action": { "type": "entity.update", "entity": "customers", "payload": {} } }
                    ]
                  }
                }
              }
            }
            """,
            revision: 3);

        copy.AddHook("work_orders", "afterUpdate", condition: null, Webhook("dispatch"));

        copy.Json.ShouldContain("entity.update");
        copy.HooksOf("work_orders").ShouldHaveSingleItem().Value.ShouldContain("dispatch");
    }

    /// <summary>Removing takes the hook at that position of that point, and no other point.</summary>
    [Fact]
    public void Removing_takes_the_hook_at_that_position()
    {
        var copy = Copy();
        copy.AddHook("work_orders", "afterCreate", condition: null, Webhook("dispatch"));
        copy.AddHook("work_orders", "afterCreate", condition: null, Webhook("billing"));
        copy.AddHook("work_orders", "afterUpdate", condition: null, Webhook("audit"));

        copy.RemoveHook("work_orders", "afterCreate", 0);

        Declared(copy, "afterCreate").ShouldContain("billing");
        Declared(copy, "afterUpdate").ShouldContain("audit");
    }

    /// <summary>
    /// Removing the last hook drops the point, and the last point drops the block.
    /// </summary>
    /// <remarks>
    /// Two empty containers are not what an author would have written, and the schema's
    /// <c>additionalProperties: false</c> on <c>hooks</c> makes an empty one look deliberate to whoever
    /// reads the file next.
    /// </remarks>
    [Fact]
    public void Removing_the_last_hook_drops_the_point_and_the_block()
    {
        var copy = Copy();
        copy.AddHook("work_orders", "afterCreate", condition: null, Webhook("dispatch"));

        copy.RemoveHook("work_orders", "afterCreate", 0);

        copy.Json.ShouldNotContain("afterCreate");
        copy.Json.ShouldNotContain("hooks");
        copy.HooksOf("work_orders").ShouldBeEmpty();
    }

    /// <summary>A point or a position nothing is at changes nothing, rather than throwing into a render.</summary>
    [Theory]
    [InlineData("afterCreate", -1)]
    [InlineData("afterCreate", 4)]
    [InlineData("beforeCreate", 0)]
    public void A_point_or_position_nothing_is_at_changes_nothing(string point, int position)
    {
        var copy = Copy();
        copy.AddHook("work_orders", "afterCreate", condition: null, Webhook("dispatch"));
        var before = copy.Json;

        copy.RemoveHook("work_orders", point, position);

        copy.Json.ShouldBe(before);
    }

    /// <summary>An entity the document does not declare is not created by hooking it.</summary>
    [Fact]
    public void An_entity_that_is_not_declared_gains_nothing()
    {
        var copy = Copy();

        copy.AddHook("invoices", "afterCreate", condition: null, Webhook("dispatch"));

        copy.Json.ShouldNotContain("invoices");
        copy.HooksOf("invoices").ShouldBeEmpty();
    }

    private static JsonObject Reject(string message) => new() { ["reject"] = message };

    private static JsonObject Webhook(string endpoint) =>
        new() { ["type"] = "webhook", ["endpoint"] = endpoint };

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

    /// <summary>One declared hook, as one line of JSON.</summary>
    private static string Declared(WorkingCopy copy, string point, int at = 0)
    {
        using var document = System.Text.Json.JsonDocument.Parse(copy.Json);
        var hooks = document.RootElement
            .GetProperty("entities").GetProperty("work_orders").GetProperty("hooks").GetProperty(point);

        return System.Text.Json.JsonSerializer.Serialize(hooks[at], _asWritten);
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
                "work_orders": { "fields": { "status": { "type": "string" } } }
              }
            }
            """,
            revision: 3);

        return copy;
    }
}
