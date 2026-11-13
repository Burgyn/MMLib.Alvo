using MMLib.Alvo.Data;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The adversarial half: two users and two tenants, through list, get, count and write, never see or reach a row
/// through a related row they could not list themselves.
/// </summary>
public abstract partial class AlvoDataRelationRuleTests
{
    /// <summary>A and B each list, get, count and write only the orders of the customers they own.</summary>
    [Fact]
    public async Task Two_users_list_get_count_and_write_only_through_their_own_related_rows()
    {
        var (data, world) = await RelationWorldAsync();
        var aOrder = await world.SeedOrderAsync(world.A, active: true);
        var bOrder = await world.SeedOrderAsync(world.B, active: true);

        var page = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Orders, IncludeTotalCount = true }, world.A, Ct);

        page.Items.Select(row => row["id"]).ShouldBe([aOrder]);
        page.TotalCount.ShouldBe(1);
        (await data.GetAsync(RelationWorld.Orders, bOrder, world.A, Ct)).ShouldBeNull();
        await ShouldBeRefusedLikeAnOwnerMissAsync(data, world, (entity, id) =>
            data.UpdateAsync(entity, id, Patch("note", "x"), world.A, cancellationToken: Ct), bOrder);
        await ShouldBeRefusedLikeAnOwnerMissAsync(data, world, (entity, id) =>
            data.DeleteAsync(entity, id, world.A, cancellationToken: Ct), bOrder);
        (await data.GetAsync(RelationWorld.Orders, bOrder, world.B, Ct)).ShouldNotBeNull();
    }

    /// <summary>A global row is reached only through the caller's own tenant's children: the related tenant scope isolates.</summary>
    [Fact]
    public async Task A_global_row_is_visible_through_the_caller_s_tenant_children_only()
    {
        var (data, world) = await RelationWorldAsync();
        var user = Guid.NewGuid();
        var region = await world.SeedGlobalAsync(RelationWorld.Regions, ("name", "north"));
        await world.SeedAsync(RelationWorld.Sites, world.Tenant1, ("region_id", region), ("manager_id", user));
        var inTenant1 = RelationWorld.Caller(user, world.Tenant1);
        var inTenant2 = RelationWorld.Caller(user, world.Tenant2);

        var first = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Regions, IncludeTotalCount = true }, inTenant1, Ct);
        var second = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Regions, IncludeTotalCount = true }, inTenant2, Ct);

        (first.TotalCount, second.TotalCount).ShouldBe((1L, 0L));
        (await data.GetAsync(RelationWorld.Regions, region, inTenant1, Ct)).ShouldNotBeNull();
        (await data.GetAsync(RelationWorld.Regions, region, inTenant2, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// One user id in two tenants: a relation that would match the other tenant's rows reaches none of them. The outer
    /// tenant scope already keeps them apart here; the related tenant scope inside the <c>EXISTS</c> is pinned by
    /// <see cref="A_global_row_is_visible_through_the_caller_s_tenant_children_only"/>. A related row of the other tenant
    /// naming one of this tenant's rows can no longer exist at all: since #161 a <c>ref</c> between two scoped entities is
    /// a key over <c>(tenant_id, id)</c>, which the other-tenant case of
    /// <see cref="An_exists_never_matches_a_related_row_the_caller_cannot_list"/> pins at the store.
    /// </summary>
    [Fact]
    public async Task Two_tenants_with_the_same_user_id_never_see_each_other_s_rows_through_a_relation()
    {
        var (data, world) = await RelationWorldAsync();
        var user = Guid.NewGuid();
        var inTenant1 = RelationWorld.Caller(user, world.Tenant1);
        var inTenant2 = RelationWorld.Caller(user, world.Tenant2);
        var order1 = await world.SeedOrderAsync(inTenant1, active: true);
        var order2 = await world.SeedOrderAsync(inTenant2, active: true);
        var project2 = await SeedProjectWithMemberAsync(world, world.Tenant2, user);

        var orders = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Orders, IncludeTotalCount = true }, inTenant1, Ct);
        var projects = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Projects, IncludeTotalCount = true }, inTenant1, Ct);

        orders.Items.Select(row => row["id"]).ShouldBe([order1]);
        (orders.TotalCount, projects.TotalCount).ShouldBe((1L, 0L));
        (await data.GetAsync(RelationWorld.Orders, order2, inTenant1, Ct)).ShouldBeNull();
        (await data.GetAsync(RelationWorld.Projects, project2, inTenant1, Ct)).ShouldBeNull();
        (await data.GetAsync(RelationWorld.Projects, project2, inTenant2, Ct)).ShouldNotBeNull();
        await ShouldBeRefusedLikeAnOwnerMissAsync(data, world, (entity, id) =>
            data.UpdateAsync(entity, id, Patch("note", "x"), inTenant1, cancellationToken: Ct), order2);
        (await data.GetAsync(RelationWorld.Orders, order2, inTenant2, Ct)).ShouldNotBeNull()["note"].ShouldBe("order");
    }

    /// <summary>
    /// Four related rows that each satisfy the body, but that the caller cannot list — another user's, one under a related
    /// entity with no list rule, another tenant's, and one behind a role the caller lacks — never make the relation match,
    /// through list, count, get or a create probe. The caller's own row is the control that it can.
    /// </summary>
    /// <remarks>
    /// <b>Another tenant's row holds at the key level since #161.</b> A pin of the other tenant naming this tenant's board
    /// used to be stored, and only the related tenant scope inside the <c>EXISTS</c> kept it from matching. A <c>ref</c>
    /// between two scoped entities is now a key over <c>(tenant_id, id)</c>, so that pin is unrepresentable: the case
    /// asserts the store refuses it exactly as a pin naming no board at all, and that the board still matches nothing.
    /// </remarks>
    /// <param name="why">Which way the related row is out of the caller's reach.</param>
    /// <param name="pinsList">The related entity's list rule in this world.</param>
    /// <param name="inOtherTenant">Whether the related row lives in the other tenant.</param>
    /// <param name="ownedByCaller">Whether the caller owns the related row.</param>
    /// <param name="visible">Whether the relation may match.</param>
    [Theory]
    [InlineData("another user's", PinsOwner, false, false, false)]
    [InlineData("no list rule", null, false, true, false)]
    [InlineData("another tenant's", PinsOwner, true, true, false)]
    [InlineData("a role the caller lacks", PinsManager, false, true, false)]
    [InlineData("the caller's own (control)", PinsOwner, false, true, true)]
    public async Task An_exists_never_matches_a_related_row_the_caller_cannot_list(
        string why, string? pinsList, bool inOtherTenant, bool ownedByCaller, bool visible)
    {
        _ = why;
        var (data, world) = await RelationWorldAsync(pinsList);
        var board = await world.SeedAsync(RelationWorld.Boards, world.Tenant1, ("name", "board"));
        var owner = (ownedByCaller ? world.A : world.B).User.Value;
        if (inOtherTenant)
        {
            await ShouldRefuseAnotherTenantsPinLikeOneNamingNoBoardAsync(world, board, owner);
        }

        var pin = inOtherTenant ? Guid.NewGuid() : await SeedPinAsync(world, world.Tenant1, board, owner);
        var page = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Boards, IncludeTotalCount = true }, world.A, Ct);
        var got = await data.GetAsync(RelationWorld.Boards, board, world.A, Ct);
        var sticker = await Record.ExceptionAsync(() => data.CreateAsync(
            RelationWorld.Stickers, Row(world, ("pin_id", pin)), world.A, cancellationToken: Ct));

        page.TotalCount.ShouldBe(visible ? 1 : 0);
        page.Items.Count.ShouldBe(visible ? 1 : 0);
        (got is not null).ShouldBe(visible);
        (sticker is null).ShouldBe(visible && !inOtherTenant);
        if (sticker is not null)
        {
            sticker.ShouldBeOfType<AlvoAuthorizationException>();
        }
    }

    /// <summary>A project is reached only by its members, through the reverse relation.</summary>
    [Fact]
    public async Task A_reverse_relation_admits_a_project_only_to_its_members()
    {
        var (data, world) = await RelationWorldAsync();
        var project = await SeedProjectWithMemberAsync(world, world.Tenant1, world.A.User.Value);
        var empty = await world.SeedAsync(RelationWorld.Projects, world.Tenant1, ("name", "nobody's"));

        var forA = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Projects, IncludeTotalCount = true }, world.A, Ct);
        var forB = await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Projects, IncludeTotalCount = true }, world.B, Ct);

        forA.Items.Select(row => row["id"]).ShouldBe([project]);
        (forA.TotalCount, forB.TotalCount).ShouldBe((1L, 0L));
        (await data.GetAsync(RelationWorld.Projects, project, world.B, Ct)).ShouldBeNull();
        (await data.GetAsync(RelationWorld.Projects, empty, world.A, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// A self-reference correlates the inner row with the <b>outer</b> row's key: an employee is read through its
    /// manager's region, never its own, and a row with no manager is never read.
    /// </summary>
    [Fact]
    public async Task A_self_reference_reads_the_outer_row_s_key()
    {
        var (data, world) = await RelationWorldAsync();
        var european = await world.SeedAsync(RelationWorld.Employees, world.Tenant1, ("region", "eu"));
        var american = await world.SeedAsync(RelationWorld.Employees, world.Tenant1, ("region", "us"));
        var underEurope = await world.SeedAsync(RelationWorld.Employees, world.Tenant1, ("region", "us"), ("manager_id", european));
        var underAmerica = await world.SeedAsync(RelationWorld.Employees, world.Tenant1, ("region", "eu"), ("manager_id", american));

        (await data.GetAsync(RelationWorld.Employees, underEurope, world.A, Ct)).ShouldNotBeNull();
        (await data.GetAsync(RelationWorld.Employees, underAmerica, world.A, Ct)).ShouldBeNull();
        (await data.GetAsync(RelationWorld.Employees, european, world.A, Ct)).ShouldBeNull();
    }

    /// <summary>A related row turning invisible to the caller hides the outer row on the very next read.</summary>
    [Fact]
    public async Task A_related_row_turning_invisible_hides_the_outer_row_on_the_next_read()
    {
        var (data, world) = await RelationWorldAsync();
        var customer = await world.SeedCustomerAsync(world.A, active: true);
        var order = await world.SeedAsync(RelationWorld.Orders, world.Tenant1, ("customer_id", customer), ("note", "order"));
        (await data.GetAsync(RelationWorld.Orders, order, world.A, Ct)).ShouldNotBeNull();

        await world.UpdateAsSeederAsync(RelationWorld.Customers, customer, world.Tenant1, "owner_id", world.B.User.Value);

        (await data.GetAsync(RelationWorld.Orders, order, world.A, Ct)).ShouldBeNull();
        (await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.Orders, IncludeTotalCount = true }, world.A, Ct)).TotalCount.ShouldBe(0);
        (await data.GetAsync(RelationWorld.Orders, order, world.B, Ct)).ShouldNotBeNull();
    }

    private const string PinsOwner = RelationWorld.PinsByOwner;

    private const string PinsManager = "'manager' in @user.roles";

    private static Task<Guid> SeedPinAsync(RelationWorld world, TenantId tenant, Guid board, Guid owner) =>
        world.SeedAsync(RelationWorld.Pins, tenant, ("board_id", board), ("color", "red"), ("owner_id", owner));

    /// <summary>
    /// Asserts the store refuses a pin of the other tenant naming <paramref name="board"/> exactly as it refuses one naming
    /// no board at all (#161) — same exception, kind, fields and message, so the refusal discloses nothing.
    /// </summary>
    private static async Task ShouldRefuseAnotherTenantsPinLikeOneNamingNoBoardAsync(RelationWorld world, Guid board, Guid owner)
    {
        var crossTenant = await Should.ThrowAsync<AlvoConstraintViolationException>(() => SeedPinAsync(world, world.Tenant2, board, owner));
        var nowhere = await Should.ThrowAsync<AlvoConstraintViolationException>(() => SeedPinAsync(world, world.Tenant2, Guid.NewGuid(), owner));

        crossTenant.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        crossTenant.Kind.ShouldBe(nowhere.Kind);
        crossTenant.Fields.ShouldBe(nowhere.Fields);
        crossTenant.Message.ShouldBe(nowhere.Message);
    }

    private static async Task<Guid> SeedProjectWithMemberAsync(RelationWorld world, TenantId tenant, Guid member)
    {
        var project = await world.SeedAsync(RelationWorld.Projects, tenant, ("name", "project"));
        await world.SeedAsync(RelationWorld.ProjectMembers, tenant, ("project_id", project), ("user_id", member));
        return project;
    }

    /// <summary>
    /// Asserts that <paramref name="act"/> on a row a relation hides fails exactly as the same call does on a row an
    /// ordinary owner rule hides — whatever exception the port gives for that today.
    /// </summary>
    private static async Task ShouldBeRefusedLikeAnOwnerMissAsync(
        IAlvoData data, RelationWorld world, Func<string, Guid, Task> act, Guid hidden)
    {
        _ = data;
        var othersNote = await world.SeedAsync(RelationWorld.Notes, world.Tenant1, ("owner_id", world.B.User.Value), ("note", "b"));
        var ownerMiss = await Record.ExceptionAsync(() => act(RelationWorld.Notes, othersNote));
        var relationMiss = await Record.ExceptionAsync(() => act(RelationWorld.Orders, hidden));

        ownerMiss.ShouldNotBeNull();
        relationMiss.ShouldNotBeNull().GetType().ShouldBe(ownerMiss.GetType());
    }

    private static Dictionary<string, object?> Row(RelationWorld world, params (string Field, object? Value)[] values)
    {
        var row = values.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal);
        row["tenant_id"] = world.Tenant1.Value;
        return row;
    }
}
