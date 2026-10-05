using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Editing a hook in place: what <c>ReplaceHook</c> writes, and what it refuses (spec §5.1).</summary>
public class WorkingCopyHookReplaceTests
{
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
    [InlineData("work_orders", "beforeUpdate", 0, true)]
    public void A_place_nothing_is_at_changes_nothing(string entity, string point, int position, bool hooksAsArray = false)
    {
        /* The guard text is the well-formed copy's hook 0, so a malformed copy is refused for its shape alone. */
        var drawn = Drawn(Copy(), 0);
        var copy = hooksAsArray ? Copy(MalformedHooks) : Copy();
        var before = copy.Json;

        copy.ReplaceHook(entity, point, position, drawn, Hook("Edited.")).ShouldBeFalse();

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

    /// <summary>
    /// The text a row draws is the text the guard compares (Task 3 review carry-over): were the tab to write a hook with
    /// options of its own that drifted from the copy's — the default encoder, or no indentation — every in-place edit
    /// would be refused as "changed elsewhere", silently. Each hook here carries what an encoder or an indenter treats
    /// differently: apostrophes, ampersands, markup, non-ASCII letters, a decimal, a nested <c>$cel</c>, a placeholder.
    /// </summary>
    [Fact]
    public void Every_hook_the_tab_draws_is_the_text_the_guard_accepts()
    {
        var copy = Copy(
            """
            {
              "apiVersion": "alvo.dev/v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" }, "total": { "type": "decimal" } },
                  "hooks": {
                    "beforeUpdate": [
                      { "condition": "old.status == 'done' && new.total > 1.50", "action": { "reject": "Zákazka <b>uzavretá</b> — ✓" } },
                      { "action": { "mutate": { "total": { "$cel": "new.total * 1.20" }, "status": "näh" } } }
                    ],
                    "afterUpdate": [
                      { "action": { "type": "webhook", "endpoint": "desk", "payload": "{{new.status}} & 'x'" } }
                    ]
                  }
                }
              }
            }
            """);

        foreach (var (point, at) in new[] { ("beforeUpdate", 0), ("beforeUpdate", 1), ("afterUpdate", 0) })
        {
            var drawn = Drawn(copy, at, point);
            var same = JsonNode.Parse(drawn)!.AsObject();

            copy.ReplaceHook("work_orders", point, at, drawn, same).ShouldBeTrue($"{point} {at} as the tab drew it:\n{drawn}");
        }
    }

    private static JsonObject Hook(string message) => new() { ["action"] = new JsonObject { ["reject"] = message } };

    /// <summary>How the On write tab draws one hook: its own reader, so the guard is tested against what a row shows.</summary>
    private static string Drawn(WorkingCopy copy, int at, string point = "beforeUpdate")
        => HooksTab.Declared(copy.HooksOf("work_orders").Single(declared => declared.Key == point).Value)[at];

    private static List<string> Messages(WorkingCopy copy)
        => [.. ((JsonArray)JsonNode.Parse(copy.Json)!["entities"]!["work_orders"]!["hooks"]!["beforeUpdate"]!)
            .Select(hook => hook!["action"]!["reject"]!.GetValue<string>())];

    /// <summary>The same hook 0, under a <c>hooks</c> that is an array rather than an object keyed by point.</summary>
    private const string MalformedHooks = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "field-service",
          "entities": {
            "work_orders": {
              "fields": { "status": { "type": "string" } },
              "hooks": [
                { "condition": "old.status == 'completed'", "action": { "reject": "First." } }
              ]
            }
          }
        }
        """;

    private static WorkingCopy Copy(string descriptor)
    {
        var copy = new WorkingCopy();
        copy.Take(descriptor, revision: 3);
        return copy;
    }

    private static WorkingCopy Copy()
        => Copy(
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
            """);
}
