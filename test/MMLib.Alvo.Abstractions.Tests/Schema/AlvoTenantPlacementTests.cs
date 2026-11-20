using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Abstractions.Tests.Schema;

/// <summary>
/// Where a created row on a scoped entity lands (#538), judged on the helper both implementations place it through:
/// the caller's tenant fills an omitted <c>tenant_id</c>, any other value is refused, and an entity or a caller the
/// rule does not reach is left alone.
/// </summary>
public class AlvoTenantPlacementTests
{
    private static readonly TenantId _acme = TenantId.New();

    private static readonly AlvoContext _acmeCaller = Caller(_acme);

    [Fact]
    public void An_omitted_tenant_id_is_filled_with_the_callers_tenant()
        => AlvoTenantPlacement.Placed(Payload(), Entity(TenancyMode.Scoped), _acmeCaller)[AlvoManagedColumns.TenantId]
            .ShouldBe(_acme.Value);

    [Fact]
    public void The_callers_own_tenant_spelled_as_text_is_accepted_and_stored_as_a_guid()
    {
        var payload = Payload(_acme.Value.ToString("D").ToUpperInvariant());

        AlvoTenantPlacement.Refusal(payload, Entity(TenancyMode.Scoped), _acmeCaller).ShouldBeNull();
        AlvoTenantPlacement.Placed(payload, Entity(TenancyMode.Scoped), _acmeCaller)[AlvoManagedColumns.TenantId]
            .ShouldBe(_acme.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-tenant")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Any_value_but_the_callers_tenant_is_refused(string? named)
        => AlvoTenantPlacement.Refusal(Payload(named), Entity(TenancyMode.Scoped), _acmeCaller)
            .ShouldBe(AlvoAuthorizationException.ForeignTenant);

    [Fact]
    public void Another_tenants_guid_is_refused_with_an_exception_that_does_not_echo_it()
    {
        var other = Guid.NewGuid();

        var refused = Should.Throw<AlvoAuthorizationException>(
            () => AlvoTenantPlacement.EnsurePlaceable(Payload(other), Entity(TenancyMode.Scoped), _acmeCaller));

        refused.Message.ShouldBe(AlvoAuthorizationException.ForeignTenant);
        refused.Message.ShouldNotContain(other.ToString());
    }

    [Theory]
    [InlineData(TenancyMode.Global)]
    [InlineData(null)]
    public void An_entity_that_is_not_scoped_is_left_alone(TenancyMode? tenancy)
    {
        var payload = Payload(Guid.NewGuid());

        AlvoTenantPlacement.Refusal(payload, Entity(tenancy), _acmeCaller).ShouldBeNull();
        AlvoTenantPlacement.Placed(payload, Entity(tenancy), _acmeCaller).ShouldBeSameAs(payload);
    }

    /// <summary>The tenant guard denies this caller before any payload is read; placement must not invent a tenant.</summary>
    [Fact]
    public void A_caller_with_no_tenant_is_left_to_the_policy_engine()
    {
        var payload = Payload(Guid.NewGuid());
        var tenantless = Caller(tenant: null);

        AlvoTenantPlacement.Refusal(payload, Entity(TenancyMode.Scoped), tenantless).ShouldBeNull();
        AlvoTenantPlacement.Placed(Payload(), Entity(TenancyMode.Scoped), tenantless)
            .ContainsKey(AlvoManagedColumns.TenantId).ShouldBeFalse();
    }

    private static EntitySchema Entity(TenancyMode? tenancy) => new()
    {
        Name = "documents",
        Tenancy = tenancy,
        Fields = [new FieldSchema { Name = "title", Type = FieldType.String }],
    };

    private static Dictionary<string, object?> Payload() =>
        new(StringComparer.Ordinal) { ["title"] = "placed" };

    private static Dictionary<string, object?> Payload(object? tenant) =>
        new(StringComparer.Ordinal) { ["title"] = "placed", [AlvoManagedColumns.TenantId] = tenant };

    private static AlvoContext Caller(TenantId? tenant) =>
        new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated }, Tenant = tenant };
}
