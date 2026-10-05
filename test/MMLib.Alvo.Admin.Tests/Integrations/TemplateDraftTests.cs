using MMLib.Alvo.Admin.Components.Integrations;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Integrations;

/// <summary>What the template sheet refuses at its fields (spec §6.2).</summary>
public class TemplateDraftTests
{
    [Theory]
    [InlineData("order-ready", null)]
    [InlineData("order_ready", null)]
    [InlineData("Order", "not a template name")]
    [InlineData("order-ready-2", null)]
    [InlineData("taken", "already declared")]
    public void A_name_is_an_identifier_and_unique(string name, string? refused)
    {
        var refusal = TemplateDraft.NameRefusal(name, ["taken"]);

        if (refused is null)
        {
            refusal.ShouldBeNull();
        }
        else
        {
            refusal.ShouldNotBeNull().ShouldContain(refused);
        }
    }

    [Theory]
    [InlineData("Your bike is ready — order {{new.order_number}}", true)]
    [InlineData("Tabs\tare one line", true)]
    [InlineData("Two\nlines", false)]
    [InlineData("Two\r\nlines", false)]
    [InlineData("Line\u2028separator", false)]
    [InlineData("Bell\u0007", false)]
    public void A_subject_is_one_line(string subject, bool accepted)
        => (TemplateDraft.SubjectRefusal(subject) is null).ShouldBe(accepted);

    [Theory]
    [InlineData("Hello {{new.contact_email}}, {{old.status}} {{event.type}} {{@user.id}}", true)]
    [InlineData("Hi {{@tenant.id}}", false)]
    [InlineData("Hi {{ @tenant.id }}", false)]
    [InlineData("Roles {{@user.roles}}", false)]
    [InlineData("Hi {{customer.name}}", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void A_body_is_required_and_names_only_roots_an_email_can_resolve(string body, bool accepted)
        => (TemplateDraft.BodyRefusal(body) is null).ShouldBe(accepted);

    [Fact]
    public void A_tenant_placeholder_is_refused_with_the_reason()
        => TemplateDraft.BodyRefusal("Hi {{@tenant.id}}").ShouldNotBeNull().ShouldContain("carries no tenant");

    [Fact]
    public void An_edit_loads_the_subject_and_body_and_does_not_refuse_its_own_name()
    {
        var draft = TemplateDraft.From("order-ready", JsonDocument.Parse("""{"subject":"Ready","body":"Hello"}""").RootElement);

        (draft.Subject, draft.Body).ShouldBe(("Ready", "Hello"));
        draft.Refusals(["order-ready"], editing: true).ShouldBeEmpty();
    }
}
