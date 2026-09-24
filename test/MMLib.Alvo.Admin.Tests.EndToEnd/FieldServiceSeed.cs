using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Rows of the field-service example the operator can see and edit, written the way a real deployment
/// would hold them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The operator is given a tenant.</b> <c>work_orders</c> and <c>customers</c> are tenant-scoped and the
/// bootstrap administrator holds none, so without one every such grid is refused before a row is read.
/// Granting it through the store — the dashboard will not let an administrator grant themselves one —
/// stands in for the second administrator a real deployment has.
/// </para>
/// <para>
/// <b>Written through the data port as that tenant</b>, because the example ships no seed.
/// </para>
/// </remarks>
internal static class FieldServiceSeed
{
    /// <summary>Gives the operator <paramref name="tenant"/>.</summary>
    public static async Task GrantTheOperatorAsync(IServiceProvider services, TenantId tenant)
    {
        var people = services.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);
        var page = await people.ListAsync(new AlvoUserQuery());
        var operatorAccount = page.Users.Single(
            person => string.Equals(person.Email, AdminWorld.AdminEmail, StringComparison.OrdinalIgnoreCase));

        await people.SetTenantAsync(operatorAccount.Id, tenant);
    }

    /// <summary>A region; <c>regions</c> is global, so its code must be unique across the world.</summary>
    public static Task<AlvoRecord> RegionAsync(IAlvoData data, AlvoContext context, string code)
        => data.CreateAsync("regions", new Dictionary<string, object?> { ["code"] = code, ["name"] = code }, context);

    /// <summary>A customer of <paramref name="tenant"/>.</summary>
    public static Task<AlvoRecord> CustomerAsync(IAlvoData data, AlvoContext context, TenantId tenant, string name)
        => data.CreateAsync(
            "customers", new Dictionary<string, object?> { ["tenant_id"] = tenant.Value, ["name"] = name, ["tier"] = "standard" },
            context);

    /// <summary>A work order of <paramref name="tenant"/>, for <paramref name="customer"/>.</summary>
    public static Task<AlvoRecord> WorkOrderAsync(
        IAlvoData data, AlvoContext context, TenantId tenant, string reference, AlvoRecord customer, AlvoRecord region)
        => data.CreateAsync(
            "work_orders",
            new Dictionary<string, object?>
            {
                ["tenant_id"] = tenant.Value,
                ["reference"] = reference,
                ["title"] = $"Service call {reference}",
                ["status"] = "scheduled",
                ["priority"] = 3,
                ["access_code"] = "1234",
                ["customer_id"] = IdOf(customer),
                ["region_id"] = IdOf(region),
            },
            context);

    /// <summary>A record's id.</summary>
    public static Guid IdOf(AlvoRecord record) => record["id"] switch
    {
        Guid id => id,
        var other => Guid.Parse(other!.ToString()!),
    };
}
