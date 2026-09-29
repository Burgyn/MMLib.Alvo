using MMLib.Alvo.Admin.Components.Access;

namespace MMLib.Alvo.Admin.Tests.Access;

/// <summary>What a person's editor changes, and whether closing it would lose anything.</summary>
public sealed class PersonDraftTests
{
    [Fact]
    public void An_untouched_draft_is_clean()
    {
        var draft = new PersonDraft(["dispatcher"], "7b2e…");

        draft.Dirty.ShouldBeFalse();
        draft.RolesChanged.ShouldBeFalse();
        draft.TenantChanged.ShouldBeFalse();
    }

    [Fact]
    public void The_same_roles_in_another_order_are_not_a_change()
    {
        var draft = new PersonDraft(["dispatcher", "technician"], null);

        draft.SetRoles(["technician", "dispatcher"]);

        draft.RolesChanged.ShouldBeFalse();
    }

    [Fact]
    public void A_role_toggled_is_a_change_and_so_is_a_tenant_typed()
    {
        var draft = new PersonDraft(["dispatcher"], null);

        draft.SetRoles(["dispatcher", "technician"]);
        draft.SetTenant("  3f0c2b8e-0000-0000-0000-000000000001 ");

        draft.RolesChanged.ShouldBeTrue();
        draft.TenantChanged.ShouldBeTrue();
        draft.Tenant.ShouldBe("3f0c2b8e-0000-0000-0000-000000000001");
        draft.Dirty.ShouldBeTrue();
    }

    [Fact]
    public void Clearing_the_tenant_box_of_a_person_who_has_none_is_not_a_change()
    {
        var draft = new PersonDraft([], null);

        draft.SetTenant("   ");

        draft.TenantChanged.ShouldBeFalse();
    }
}
