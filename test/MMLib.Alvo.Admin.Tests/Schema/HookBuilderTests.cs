using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The hook editor's values, and the action built from them in the frozen schema's shape.
/// </summary>
public class HookBuilderTests
{
    [Theory]
    [InlineData("beforeCreate", new[] { "reject", "mutate" })]
    [InlineData("beforeUpdate", new[] { "reject", "mutate" })]
    [InlineData("beforeDelete", new[] { "reject" })]
    [InlineData("afterCreate", new[] { "webhook", "email", "function" })]
    [InlineData("afterDelete", new[] { "webhook", "email", "function" })]
    public void A_point_admits_only_the_kinds_the_apply_accepts_there(string point, string[] kinds)
    {
        var hook = new HookBuilder();
        hook.Choose(point);

        hook.Kinds.ShouldBe(kinds);
    }

    [Fact]
    public void Moving_to_a_point_that_does_not_admit_the_kind_moves_the_kind_to_one_it_does()
    {
        var hook = new HookBuilder();
        hook.Choose("afterCreate");
        hook.Kind = HookBuilder.Email;

        hook.Choose("beforeCreate");

        hook.Kind.ShouldBe(HookBuilder.Reject);
    }

    [Fact]
    public void A_kind_the_new_point_admits_is_kept()
    {
        var hook = new HookBuilder { Kind = HookBuilder.Mutate };

        hook.Choose("beforeUpdate");

        hook.Kind.ShouldBe(HookBuilder.Mutate);
    }

    [Theory]
    [InlineData("reject", "A reject carries the message the caller reads. Write one.")]
    [InlineData("mutate", "A mutate patches at least one field. Name the field and what to set it to.")]
    [InlineData("webhook", "A webhook names an endpoint declared under 'webhooks.endpoints'.")]
    [InlineData("email", "An email names a template and who it goes to.")]
    public void An_action_missing_what_the_schema_requires_is_refused(string kind, string refusal)
    {
        var hook = new HookBuilder { Kind = kind, MutateRows = { new MutateRow("status", MutateMode.Expression, string.Empty) }, Template = "done" };

        hook.Build(out var said).ShouldBeNull();
        said.ShouldBe(refusal);
    }

    [Fact]
    public void A_kind_the_editor_does_not_know_cannot_build_an_empty_email()
    {
        var hook = new HookBuilder { Kind = "sms" };

        hook.Build(out var refusal).ShouldBeNull();
        refusal.ShouldBe("An email names a template and who it goes to.");
    }

    [Fact]
    public void A_reject_carries_its_message()
        => Built(new HookBuilder { RejectMessage = "No." }).ShouldBe("""{"reject":"No."}""");

    [Fact]
    public void A_mutate_stores_its_value_as_cel()
        => Built(new HookBuilder
        {
            Kind = HookBuilder.Mutate,
            MutateRows = { new MutateRow("completed_on", MutateMode.Expression, "now()") },
            Fields = new Dictionary<string, FieldSchema>(StringComparer.Ordinal)
            {
                ["completed_on"] = new() { Name = "completed_on", Type = FieldType.DateTime },
            },
        }).ShouldBe("""{"mutate":{"completed_on":{"$cel":"now()"}}}""");

    [Fact]
    public void A_webhook_names_its_endpoint()
        => Built(new HookBuilder { Kind = HookBuilder.Webhook, Endpoint = "dispatch" })
            .ShouldBe("""{"type":"webhook","endpoint":"dispatch"}""");

    [Fact]
    public void An_email_names_its_template_and_recipient()
        => Built(new HookBuilder { Kind = HookBuilder.Email, Template = "done", To = "{{new.email}}" })
            .ShouldBe("""{"type":"email","template":"done","to":"{{new.email}}"}""");

    [Theory]
    [InlineData("beforeCreate", "new.priority == 'high'", "<code class=\"a-mono\">new</code>")]
    [InlineData("beforeDelete", "old.status == 'completed'", "<code class=\"a-mono\">old</code>")]
    public void The_guidance_names_only_the_row_images_the_point_has(string point, string example, string images)
    {
        var hook = new HookBuilder();
        hook.Choose(point);

        hook.Example.ShouldBe(example);
        hook.Images.ShouldBe(images);
    }

    /// <summary>Apply refuses <c>{{old.…}}</c> at afterCreate and <c>{{new.…}}</c> at afterDelete (HX-01), so the examples never write one.</summary>
    [Theory]
    [InlineData("afterCreate", "[\"{{new.order_number}}\", {{new.total}}]", "{{new.contact_email}}")]
    [InlineData("afterUpdate", "[\"{{new.order_number}}\", {{new.total}}]", "{{new.contact_email}}")]
    [InlineData("afterDelete", "[\"{{old.order_number}}\", {{old.total}}]", "{{old.contact_email}}")]
    public void The_placeholder_examples_read_only_an_image_the_point_s_event_carries(string point, string payload, string recipient)
    {
        var hook = new HookBuilder();
        hook.Choose(point);

        hook.PayloadExample.ShouldBe(payload);
        hook.RecipientExample.ShouldBe(recipient);
    }

    [Fact]
    public void Clearing_keeps_the_point_and_the_kind_for_the_next_hook()
    {
        var hook = new HookBuilder { Condition = "new.a == 1", RejectMessage = "No." };
        hook.Choose("beforeUpdate");

        hook.Clear();

        (hook.Point, hook.Kind, hook.Condition, hook.RejectMessage)
            .ShouldBe(("beforeUpdate", HookBuilder.Reject, string.Empty, string.Empty));
    }

    private static string Built(HookBuilder hook)
    {
        var action = hook.Build(out var refusal);
        refusal.ShouldBeNull();
        return action.ShouldNotBeNull().ToJsonString();
    }

    [Fact]
    public void Function_is_not_offered_before_a_write()
    {
        foreach (var point in new[] { "beforeCreate", "beforeUpdate", "beforeDelete" })
        {
            var hook = new HookBuilder();
            hook.Choose(point);

            hook.Kinds.ShouldNotContain(HookBuilder.Function);
        }
    }

    [Fact]
    public void A_function_hook_builds_its_input_object()
    {
        var hook = FunctionHook();
        Row(hook, "orderId").Text = "ORD-1";
        Row(hook, "amount").Mode = MutateMode.Expression;
        Row(hook, "amount").Text = "new.total";

        var action = hook.Build(out var refusal);

        action.ShouldNotBeNull(refusal).ToJsonString().ShouldBe(
            """{"type":"function","name":"issue-invoice","input":{"amount":{"$cel":"new.total"},"orderId":"ORD-1","note":null}}""");
    }

    [Fact]
    public void A_required_input_left_empty_is_refused()
    {
        var hook = FunctionHook();
        Row(hook, "amount").Text = "12.5";

        hook.Build(out var refusal).ShouldBeNull();
        refusal.ShouldBe("'orderId' is required by issue-invoice.");
    }

    [Fact]
    public void A_nullable_required_input_left_empty_is_written_as_null_and_an_optional_one_is_omitted()
    {
        var hook = FunctionHook();
        Row(hook, "orderId").Text = "ORD-1";
        Row(hook, "amount").Text = "3";

        hook.Build(out _).ShouldNotBeNull()["input"]!.ToJsonString().ShouldBe("""{"amount":3,"orderId":"ORD-1","note":null}""");
    }

    [Fact]
    public void A_literal_the_input_type_cannot_hold_is_refused()
    {
        var hook = FunctionHook();
        Row(hook, "orderId").Text = "ORD-1";
        Row(hook, "amount").Text = "twelve";

        hook.Build(out var refusal).ShouldBeNull();
        refusal.ShouldBe("'amount' takes number; 'twelve' is not one.");
    }

    [Fact]
    public void A_function_hook_without_a_function_is_refused()
    {
        var hook = new HookBuilder();
        hook.Choose("afterCreate");
        hook.Kind = HookBuilder.Function;

        hook.Build(out var refusal).ShouldBeNull();
        refusal.ShouldBe("A function action names a function the host registered with AddFunction.");
    }

    private const string InvoiceSchema = """
        {"type":"object","properties":{"orderId":{"type":"string"},"amount":{"type":"number"},"note":{"type":["string","null"]},"memo":{"type":"string"}},"required":["orderId","amount","note"]}
        """;

    private static HookBuilder FunctionHook()
    {
        var hook = new HookBuilder();
        hook.Choose("afterCreate");
        hook.Kind = HookBuilder.Function;
        hook.ChooseFunction("issue-invoice", System.Text.Json.JsonDocument.Parse(InvoiceSchema).RootElement);
        return hook;
    }

    private static FunctionInputRow Row(HookBuilder hook, string name) => hook.InputRows.Single(row => row.Name == name);
}
