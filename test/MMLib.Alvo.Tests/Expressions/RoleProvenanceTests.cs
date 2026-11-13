using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

using static MMLib.Alvo.Tests.Expressions.TenantRoleCallers;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Which role set <c>@user.roles</c> is on a decision's path (spec §6.2): project roles plus tenant roles only while
/// every entity on the path is scoped, project roles alone everywhere else — and the very set the caller carries,
/// unallocated, whenever the tenant roles cannot count.
/// </summary>
public sealed class RoleProvenanceTests
{
    [Fact]
    public void A_scoped_entity_sees_project_and_tenant_roles()
    {
        var visible = RoleProvenance.Visible(Person, TenancyMode.Scoped);

        visible.SetEquals([Role.Authenticated, Dispatcher.Single()]).ShouldBeTrue();
    }

    [Theory]
    [InlineData(TenancyMode.Global)]
    [InlineData(null)]
    public void A_global_or_untenanted_scope_sees_project_roles_only(TenancyMode? tenancy)
    {
        RoleProvenance.Visible(Person, tenancy).ShouldBeSameAs(Person.Roles);
    }

    [Fact]
    public void Tenant_roles_without_a_tenant_count_nowhere()
    {
        var tenantless = Person with { Tenant = null };

        RoleProvenance.Visible(tenantless, TenancyMode.Scoped).ShouldBeSameAs(tenantless.Roles);
    }

    [Fact]
    public void A_caller_without_tenant_roles_allocates_nothing()
    {
        RoleProvenance.Visible(Plain, TenancyMode.Scoped).ShouldBeSameAs(Plain.Roles);
    }

    [Theory]
    [InlineData(new[] { TenancyMode.Scoped }, true)]
    [InlineData(new[] { TenancyMode.Scoped, TenancyMode.Scoped, TenancyMode.Scoped }, true)]
    [InlineData(new[] { TenancyMode.Global, TenancyMode.Scoped }, false)]
    [InlineData(new[] { TenancyMode.Scoped, TenancyMode.Global }, false)]
    [InlineData(new[] { TenancyMode.Scoped, TenancyMode.Global, TenancyMode.Scoped }, false)]
    public void A_path_counts_tenant_roles_only_while_every_entity_on_it_is_scoped(TenancyMode[] path, bool counts)
    {
        var scoped = path.Aggregate(true, (soFar, entity) => RoleProvenance.StillScoped(soFar, entity));

        scoped.ShouldBe(counts);
        RoleProvenance.OnPath(Person, scoped).Contains(Dispatcher.Single()).ShouldBe(counts);
    }

    [Fact]
    public void An_unscoped_path_returns_the_project_roles_themselves()
    {
        RoleProvenance.OnPath(Person, everyEntityScoped: false).ShouldBeSameAs(Person.Roles);
    }
}
