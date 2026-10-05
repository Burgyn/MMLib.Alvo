using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The hook editor opening a declared hook, and what Save writes back (spec §4.2, §5.2, §5.3, B1, B3, D1, D2, D4).</summary>
public class HookBuilderEditTests
{
    private static readonly IReadOnlyDictionary<string, FieldSchema> _fields = new Dictionary<string, FieldSchema>(StringComparer.Ordinal)
    {
        ["completed_at"] = new() { Name = "completed_at", Type = FieldType.DateTime },
        ["status"] = new() { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"], Required = true },
        ["quantity"] = new() { Name = "quantity", Type = FieldType.Integer },
        ["paid"] = new() { Name = "paid", Type = FieldType.Boolean },
        ["note"] = new() { Name = "note", Type = FieldType.String },
        ["price"] = new() { Name = "price", Type = FieldType.Decimal },
    };

    [Theory]
    [InlineData("beforeUpdate", """{"condition":"old.status == 'closed'","action":{"reject":"Closed."}}""")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"completed_at":{"$cel":"now()"},"status":"open","quantity":3,"paid":true,"note":null}}}""")]
    [InlineData("afterCreate", """{"action":{"type":"webhook","endpoint":"desk","payload":"[{{new.quantity}}]"},"condition":"new.paid == true"}""")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"done","to":"{{new.note}}"}}""")]
    public void A_drawable_hook_saved_unchanged_is_written_back_byte_for_byte(string point, string json)
    {
        var original = Parse(json);
        var builder = HookBuilder.From(point, original, _fields).ShouldNotBeNull();

        var saved = builder.BuildHook(original, out var refusal);

        saved.ShouldNotBeNull(refusal).ToJsonString(Relaxed.Options).ShouldBe(original.ToJsonString(Relaxed.Options));
    }

    /// <summary>Apply reads an exponent as a decimal (<c>TryGetDecimal</c>), so the editor opens it and Save leaves it as declared.</summary>
    [Fact]
    public void A_declared_decimal_in_exponent_form_saves_unchanged()
    {
        var original = Parse("""{"action":{"mutate":{"price":1e2}}}""");
        var builder = HookBuilder.From("beforeUpdate", original, _fields).ShouldNotBeNull();

        builder.BuildHook(original, out var refusal).ShouldNotBeNull(refusal).ToJsonString(Relaxed.Options)
            .ShouldBe("""{"action":{"mutate":{"price":1e2}}}""");
    }

    [Fact]
    public void A_loaded_hook_fixes_its_point()
    {
        var builder = HookBuilder.From("beforeUpdate", Parse("""{"action":{"reject":"x"}}"""), _fields)!;

        builder.Choose("afterCreate");

        (builder.Point, builder.PointLocked, builder.Kind).ShouldBe(("beforeUpdate", true, HookBuilder.Reject));
    }

    [Fact]
    public void A_loaded_mutate_keeps_each_fields_mode_and_order()
        => HookBuilder.From(
                "beforeUpdate",
                Parse("""{"action":{"mutate":{"completed_at":{"$cel":"now()"},"status":"open","note":null}}}"""),
                _fields)!
            .MutateRows.Select(row => (row.Field, row.Mode, row.Text, row.Empty)).ShouldBe(
            [
                ("completed_at", MutateMode.Expression, "now()", false),
                ("status", MutateMode.Literal, "open", false),
                ("note", MutateMode.Literal, string.Empty, true),
            ]);

    [Fact]
    public void A_shape_the_editor_cannot_draw_does_not_load()
        => HookBuilder.From("afterUpdate", Parse("""{"action":{"type":"email","template":"t","to":"a@b.c","data":"{{new.a}}"}}"""), _fields)
            .ShouldBeNull();

    [Fact]
    public void Editing_a_webhook_keeps_its_key_order_and_clearing_the_payload_removes_it()
    {
        var original = Parse("""{"action":{"type":"webhook","endpoint":"desk","payload":"[1]"},"condition":"new.paid == true"}""");
        var builder = HookBuilder.From("afterCreate", original, _fields)!;
        builder.Endpoint = "billing";
        builder.Payload = string.Empty;

        builder.BuildHook(original, out _)!.ToJsonString(Relaxed.Options)
            .ShouldBe("""{"action":{"type":"webhook","endpoint":"billing"},"condition":"new.paid == true"}""");
    }

    [Fact]
    public void Switching_the_kind_replaces_the_action_and_drops_what_belonged_to_the_old_one()
    {
        var original = Parse("""{"action":{"type":"webhook","endpoint":"desk","payload":"[1]"}}""");
        var builder = HookBuilder.From("afterCreate", original, _fields)!;
        builder.Kind = HookBuilder.Email;
        builder.Template = "done";
        builder.To = "ops@example.com";

        builder.BuildHook(original, out _)!.ToJsonString(Relaxed.Options)
            .ShouldBe("""{"action":{"type":"email","template":"done","to":"ops@example.com"}}""");
    }

    [Theory]
    [InlineData("quantity", "1.5", "whole number")]
    [InlineData("status", "bogus", "open, closed")]
    [InlineData("missing", "x", "not a field")]
    public void A_mutate_row_that_cannot_be_written_is_refused_with_its_reason(string field, string text, string says)
    {
        var builder = new HookBuilder { Kind = HookBuilder.Mutate, Fields = _fields, MutateRows = { new MutateRow(field, MutateMode.Literal, text) } };

        builder.Build(out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNull().ShouldContain(says);
    }

    [Fact]
    public void A_field_patched_twice_is_refused()
    {
        var builder = new HookBuilder
        {
            Kind = HookBuilder.Mutate,
            Fields = _fields,
            MutateRows = { new MutateRow("note", MutateMode.Literal, "a"), new MutateRow("note", MutateMode.Expression, "'b'") },
        };

        builder.Build(out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNull().ShouldContain("patched twice");
    }

    [Fact]
    public void Several_fields_are_one_mutate_a_literal_beside_an_expression()
    {
        var builder = new HookBuilder
        {
            Kind = HookBuilder.Mutate,
            Fields = _fields,
            MutateRows = { new MutateRow("paid", MutateMode.Literal, "true"), new MutateRow("note", MutateMode.Expression, "'Paid in full'") },
        };

        builder.Build(out _)!.ToJsonString(Relaxed.Options).ShouldBe("""{"mutate":{"paid":true,"note":{"$cel":"'Paid in full'"}}}""");
    }

    /// <summary>
    /// An action slot is checked without the condition (spec D4): the after-hook compiler skips the action when the
    /// condition fails, so a broken condition in the candidate would read as a green payload.
    /// </summary>
    [Fact]
    public void A_candidate_for_an_action_slot_carries_no_condition_so_a_broken_one_cannot_silence_its_check()
    {
        var builder = new HookBuilder { Kind = HookBuilder.Webhook, Endpoint = "desk", Payload = "[1]", Condition = "new.nope ==" };
        builder.Choose("afterCreate");

        builder.CandidateHook(original: null).ToJsonString(Relaxed.Options)
            .ShouldBe("""{"action":{"type":"webhook","endpoint":"desk","payload":"[1]"}}""");
    }

    [Fact]
    public void A_candidate_on_an_opened_hook_drops_its_declared_condition_too()
    {
        var original = Parse("""{"condition":"new.paid == true","action":{"type":"webhook","endpoint":"desk"}}""");
        var builder = HookBuilder.From("afterCreate", original, _fields)!;

        builder.CandidateHook(original).ContainsKey("condition").ShouldBeFalse();
        original.ContainsKey("condition").ShouldBeTrue("the declared hook is not modified");
    }

    [Fact]
    public void The_fingerprint_moves_with_anything_typed_and_not_without()
    {
        var builder = HookBuilder.From("beforeUpdate", Parse("""{"action":{"reject":"x"}}"""), _fields)!;
        var opened = builder.Fingerprint();

        builder.Fingerprint().ShouldBe(opened);
        builder.RejectMessage = "y";
        builder.Fingerprint().ShouldNotBe(opened);
    }

    /// <summary>
    /// Choosing mutate shows a blank row; choosing reject again must not leave the edit dirty because of it (Task 8
    /// review, minor 3): a blank row is no input, here as in <see cref="HookBuilder.HasInput"/>.
    /// </summary>
    [Fact]
    public void A_blank_row_left_by_a_kind_round_trip_does_not_move_the_fingerprint()
    {
        var builder = HookBuilder.From("beforeUpdate", Parse("""{"action":{"reject":"x"}}"""), _fields)!;
        var opened = builder.Fingerprint();

        builder.Kind = HookBuilder.Mutate;
        builder.MutateRows.Add(new MutateRow());
        builder.Kind = HookBuilder.Reject;

        builder.Fingerprint().ShouldBe(opened);
        builder.MutateRows[0].Text = "1";
        builder.Fingerprint().ShouldNotBe(opened);
    }

    /// <summary>A declared <c>"3"</c> on an integer field is refused on Save, not silently written back as <c>3</c>.</summary>
    [Fact]
    public void A_declared_literal_of_another_json_kind_is_refused_on_save()
    {
        var original = Parse("""{"action":{"mutate":{"quantity":"3"}}}""");
        var builder = HookBuilder.From("beforeUpdate", original, _fields)!;

        builder.BuildHook(original, out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNull().ShouldContain("'quantity'");
    }

    [Theory]
    [InlineData("total", "total")]
    [InlineData(" total ", "total")]
    [InlineData("a/b", "a/b")]
    public void A_row_s_check_slot_is_keyed_as_build_stages_it(string field, string key)
    {
        var row = new MutateRow(field, MutateMode.Expression, "1");

        HookBuilder.MutateSlot(row).ShouldBe(["action", HookBuilder.Mutate, key]);
        HookBuilder.MutateKey(row).ShouldBe(key);
    }

    [Fact]
    public void A_fresh_builder_has_no_input_until_something_is_typed()
    {
        var builder = new HookBuilder();
        builder.HasInput.ShouldBeFalse();

        builder.MutateRows.Add(new MutateRow());
        builder.HasInput.ShouldBeFalse();

        builder.MutateRows[0].Text = "1";
        builder.HasInput.ShouldBeTrue();
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
