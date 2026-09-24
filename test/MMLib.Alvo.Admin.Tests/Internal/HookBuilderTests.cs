using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The hook editor's values, and the action built from them in the frozen schema's shape.
/// </summary>
public class HookBuilderTests
{
    [Theory]
    [InlineData("beforeCreate", new[] { "reject", "mutate" })]
    [InlineData("beforeUpdate", new[] { "reject", "mutate" })]
    [InlineData("beforeDelete", new[] { "reject" })]
    [InlineData("afterCreate", new[] { "webhook", "email" })]
    [InlineData("afterDelete", new[] { "webhook", "email" })]
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
        var hook = new HookBuilder { Kind = kind, MutateField = "status", Template = "done" };

        hook.Build(out var said).ShouldBeNull();
        said.ShouldBe(refusal);
    }

    [Fact]
    public void A_reject_carries_its_message()
        => Built(new HookBuilder { RejectMessage = "No." }).ShouldBe("""{"reject":"No."}""");

    [Fact]
    public void A_mutate_stores_its_value_as_cel()
        => Built(new HookBuilder { Kind = HookBuilder.Mutate, MutateField = "completed_on", MutateValue = "now()" })
            .ShouldBe("""{"mutate":{"completed_on":{"$cel":"now()"}}}""");

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
}
