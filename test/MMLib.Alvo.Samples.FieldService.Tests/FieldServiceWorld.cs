using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.TestHost;

namespace MMLib.Alvo.Samples.FieldService.Tests;

/// <summary>The field-service host's slices over <see cref="AlvoTestHost"/>, and the rows every test seeds.</summary>
internal static class FieldServiceWorld
{
    /// <summary>Friday 13 November 2026, 08:00 UTC — 09:00 in Bratislava.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    public static readonly TenantId North = new(Guid.Parse("7e9a1c00-0001-4000-8000-00000000000a"));

    public static readonly TenantId South = new(Guid.Parse("7e9a1c00-0002-4000-8000-00000000000b"));

    public static readonly Guid Technician = Guid.Parse("0a51c4e1-0002-4000-8000-000000000002");

    public static readonly Guid OtherTechnician = Guid.Parse("0a51c4e1-0003-4000-8000-000000000003");

    public static string Descriptor => Path.Combine(AppContext.BaseDirectory, "field-service.alvo.json");

    public static Task<AlvoTestHost> StartAsync(CancellationToken cancellationToken, FakeTimeProvider? time = null) =>
        AlvoTestHost.StartAsync(builder => Compose(builder), time ?? new FakeTimeProvider(Now), cancellationToken: cancellationToken);

    /// <summary>The host's three composition steps over a test's builder: the same calls <c>FieldServiceHost</c> makes.</summary>
    public static IAlvoBuilder Compose(IAlvoBuilder builder)
    {
        var configuration = Configuration();
        builder.FromDescriptor(Descriptor).AddFieldService(FieldServiceSetup.ReadOptions(configuration));
        builder.Services.AddFieldServiceServices(configuration);
        return builder;
    }

    public static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldService:TimeZone"] = "Europe/Bratislava",
            ["FieldService:NotificationsUrl"] = "http://localhost:9090/notifications",
            ["FieldService:Tenants:0"] = North.Value.ToString(),
            ["FieldService:Tenants:1"] = South.Value.ToString(),
        })
        .Build();

    public static AlvoContext Admin(TenantId tenant) => AlvoTestHost.Caller(FieldServiceRoles.Admin) with { Tenant = tenant };

    public static AlvoContext Dispatcher(TenantId tenant) => AlvoTestHost.Caller(FieldServiceRoles.Dispatcher) with { Tenant = tenant };

    public static AlvoContext TechnicianIn(TenantId tenant) =>
        AlvoTestHost.Caller(FieldServiceRoles.Technician) with { Tenant = tenant, User = new UserId(Technician) };

    public static IAlvoSession Session(this AlvoTestHost alvo, AlvoContext caller) =>
        alvo.Services.GetRequiredService<IAlvoSessions>().For(caller);

    public static async Task<Seeded> SeedAsync(this AlvoTestHost alvo, TenantId tenant, CancellationToken cancellationToken, long? opensHour = 7, long? closesHour = 19)
    {
        var region = await alvo.Data.CreateAsync(
            "regions",
            new Dictionary<string, object?> { ["code"] = $"R-{Guid.NewGuid():N}"[..12], ["name"] = "North" },
            Admin(tenant),
            cancellationToken: cancellationToken);
        var customer = await alvo.Session(Dispatcher(tenant)).CreateAsync(
            Customer.Create("Kaviareň Dunaj", CustomerTier.Standard, serviceOpensHour: opensHour, serviceClosesHour: closesHour, tenantId: tenant.Value),
            cancellationToken);

        return new Seeded(tenant, (Guid)region["id"]!, customer!.Id);
    }

    public static async Task<WorkOrder> CreateJobAsync(
        this AlvoTestHost alvo,
        Seeded seed,
        string reference,
        CancellationToken cancellationToken,
        WorkOrderStatus status = WorkOrderStatus.New,
        bool? isEmergency = null,
        DateTimeOffset? respondBy = null,
        DateTimeOffset? scheduledFor = null,
        Guid? assignedTo = null,
        long priority = 3)
    {
        var job = WorkOrder.Create(
            reference,
            $"Job {reference}",
            status,
            priority,
            seed.CustomerId,
            seed.RegionId,
            isEmergency: isEmergency,
            respondBy: respondBy,
            scheduledFor: scheduledFor,
            assignedTo: assignedTo,
            contactEmail: $"{reference.ToLowerInvariant()}@example.com",
            tenantId: seed.Tenant.Value);

        var created = await alvo.Session(Dispatcher(seed.Tenant)).CreateAsync(WithAccessCode(job), cancellationToken);
        return created!;
    }

    /// <summary>
    /// The generated <c>Create</c> takes no hidden field, and <c>access_code</c> is required AND hidden: the wrapper keeps
    /// any field it does not declare in its record, so the code rides along in it.
    /// </summary>
    private static WorkOrder WithAccessCode(WorkOrder job) => new(job.Record.With("access_code", "4711"));
}

/// <summary>The rows <see cref="FieldServiceWorld.SeedAsync"/> created in one tenant.</summary>
internal sealed record Seeded(TenantId Tenant, Guid RegionId, Guid CustomerId);
