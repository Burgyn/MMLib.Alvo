using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Editing a hook in place: what <c>ReplaceHook</c> writes, and what it refuses (spec §5.1).</summary>
public class WorkingCopyHookReplaceTests
{
    /// <summary>How the On write tab draws one hook — <c>HooksTab._readable</c>'s options.</summary>
    private static readonly JsonSerializerOptions _drawn = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void The_hook_at_its_position_is_replaced_and_the_others_keep_their_places()
    {
        var copy = Copy();

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, Drawn(copy, 0), Hook("Edited.")).ShouldBeTrue();

        Messages(copy).ShouldBe(["Edited.", "Second."]);
    }

    [Fact]
    public void A_hook_that_changed_since_it_was_drawn_is_left_alone()
    {
        var copy = Copy();
        var drawn = Drawn(copy, 0);
        copy.ReplaceHook("work_orders", "beforeUpdate", 0, drawn, Hook("Another tab.")).ShouldBeTrue();
        var before = copy.Json;

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, drawn, Hook("Stale.")).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Theory]
    [InlineData("work_orders", "beforeUpdate", 2)]
    [InlineData("work_orders", "beforeUpdate", -1)]
    [InlineData("work_orders", "afterCreate", 0)]
    [InlineData("invoices", "beforeUpdate", 0)]
    public void A_place_nothing_is_at_changes_nothing(string entity, string point, int position)
    {
        var copy = Copy();
        var before = copy.Json;

        copy.ReplaceHook(entity, point, position, Drawn(copy, 0), Hook("Edited.")).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void The_replacement_is_a_copy_so_a_later_change_to_the_argument_does_not_reach_the_document()
    {
        var copy = Copy();
        var hook = Hook("Edited.");

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, Drawn(copy, 0), hook).ShouldBeTrue();
        hook["action"]!["reject"] = "Changed afterwards.";

        Messages(copy)[0].ShouldBe("Edited.");
    }

    [Fact]
    public void A_replacement_is_a_pending_edit_and_raises_one_change()
    {
        var copy = Copy();
        var raised = 0;
        copy.Changed += () => raised++;

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, Drawn(copy, 0), Hook("Edited.")).ShouldBeTrue();

        raised.ShouldBe(1);
        copy.PendingCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void An_apostrophe_the_editor_writes_survives_into_the_document_and_the_text_the_guard_compares()
    {
        var copy = Copy();
        var edited = new JsonObject
        {
            ["condition"] = "old.status == 'done'",
            ["action"] = new JsonObject { ["reject"] = "Edited." },
        };
        copy.ReplaceHook("work_orders", "beforeUpdate", 0, Drawn(copy, 0), edited).ShouldBeTrue();

        /* Spelled out by hand, not re-serialised, so only the copy's own writer decides whether it matches. */
        var drawn = """
            {
              "condition": "old.status == 'done'",
              "action": {
                "reject": "Edited."
              }
            }
            """.ReplaceLineEndings();

        copy.Json.ShouldContain("\"condition\": \"old.status == 'done'\"");
        copy.ReplaceHook("work_orders", "beforeUpdate", 0, drawn, Hook("Again.")).ShouldBeTrue();
    }

    private static JsonObject Hook(string message) => new() { ["action"] = new JsonObject { ["reject"] = message } };

    private static string Drawn(WorkingCopy copy, int at)
        => ((JsonArray)JsonNode.Parse(copy.HooksOf("work_orders").Single(point => point.Key == "beforeUpdate").Value)!)[at]!
            .ToJsonString(_drawn);

    private static List<string> Messages(WorkingCopy copy)
        => [.. ((JsonArray)JsonNode.Parse(copy.Json)!["entities"]!["work_orders"]!["hooks"]!["beforeUpdate"]!)
            .Select(hook => hook!["action"]!["reject"]!.GetValue<string>())];

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "alvo.dev/v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" } },
                  "hooks": {
                    "beforeUpdate": [
                      { "condition": "old.status == 'completed'", "action": { "reject": "First." } },
                      { "action": { "reject": "Second." } }
                    ]
                  }
                }
              }
            }
            """,
            revision: 3);

        return copy;
    }
}
