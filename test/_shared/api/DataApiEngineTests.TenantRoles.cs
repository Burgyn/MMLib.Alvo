using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Spec §6.2 over HTTP, on every engine: a tenant role counts only while every entity on a decision's path is scoped —
/// in a scoped entity's rules and in a relation term into a scoped entity, never in a global entity's rules, never past
/// a global hop, and never from a global entity into a scoped one.
/// </summary>
/// <remarks>
/// Each refusal has its control: the same role held as a <em>project</em> role (<see cref="_projectDispatcher"/>) passes
/// the same rule on the same row, so a refusal of the tenant role is the path rule, never a rule that admits nobody.
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private const string AcmeDispatcherToken = "acme-dispatcher";
    private const string AcmePlainToken = "acme-plain";

    private static readonly Guid _acme = Guid.NewGuid();

    private static readonly TestApiKey _projectDispatcher =
        new("tr-dispatcher", ["dispatcher", "authenticated"], ["*:read", "*:write"], _acme);

    private static readonly KeyValuePair<string, string>[] _tenantDispatcher = Bearer(AcmeDispatcherToken);

    private static readonly KeyValuePair<string, string>[] _tenantPlain = Bearer(AcmePlainToken);

    [Fact]
    public async Task A_tenant_role_admits_the_rules_of_a_scoped_entity_on_every_engine()
    {
        await using var world = await TenantRolesWorldAsync();

        using var created = await SendAsPersonAsync(world, HttpMethod.Post, "/api/work_orders", _tenantDispatcher, WorkOrder());
        using var listed = await SendAsPersonAsync(world, HttpMethod.Get, "/api/work_orders", _tenantDispatcher);
        using var plainListed = await SendAsPersonAsync(world, HttpMethod.Get, "/api/work_orders", _tenantPlain);
        using var plainCreated = await SendAsPersonAsync(world, HttpMethod.Post, "/api/work_orders", _tenantPlain, WorkOrder());

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await ItemsAsync(listed)).Count.ShouldBe(1);
        (await ItemsAsync(plainListed)).ShouldBeEmpty("without the tenant role the same rule admits nothing");
        plainCreated.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await plainCreated.ReadTextAsync());
    }

    [Fact]
    public async Task A_tenant_role_never_admits_the_rules_of_a_global_entity_on_every_engine()
    {
        await using var world = await TenantRolesWorldAsync();
        var region = await SeedAsync(world, "regions", Region("N"));

        using var created = await SendAsPersonAsync(world, HttpMethod.Post, "/api/regions", _tenantDispatcher, Region("S"));
        using var listed = await SendAsPersonAsync(world, HttpMethod.Get, "/api/regions", _tenantDispatcher);
        using var read = await SendAsPersonAsync(world, HttpMethod.Get, $"/api/regions/{region}", _tenantDispatcher);

        created.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await created.ReadTextAsync());
        (await ItemsAsync(listed)).ShouldBeEmpty();
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound, await read.ReadTextAsync());
        (await world.CountRowsAsync("regions")).ShouldBe(1, "the refused create stored nothing");
    }

    [Fact]
    public async Task A_relation_term_sees_a_tenant_role_only_into_a_scoped_entity_on_every_engine()
    {
        await using var world = await TenantRolesWorldAsync();
        var region = await SeedAsync(world, "regions", Region("N"));
        var workOrder = await SeedAsync(world, "work_orders", WorkOrder());
        await SeedAsync(world, "visits", Scoped(new JsonObject { ["work_order_id"] = workOrder }));
        await SeedAsync(world, "stops", Scoped(new JsonObject { ["region_id"] = region }));

        using var visits = await SendAsPersonAsync(world, HttpMethod.Get, "/api/visits", _tenantDispatcher);
        using var stops = await SendAsPersonAsync(world, HttpMethod.Get, "/api/stops", _tenantDispatcher);
        using var projectStops = await world.SendAsync(HttpMethod.Get, "/api/stops", _projectDispatcher);

        (await ItemsAsync(visits)).Count.ShouldBe(1, "scoped visits into scoped work_orders: the tenant role counts");
        (await ItemsAsync(stops)).ShouldBeEmpty("scoped stops into global regions: the tenant role stops at the global hop");
        (await ItemsAsync(projectStops)).Count.ShouldBe(1, "the same hop admits the role held as a project role");
    }

    /// <summary>
    /// The amended path rule (spec §6.2): a global entity's rule that reaches a scoped entity through a relation term
    /// never sees a tenant role, not even through the scoped entity's own <c>list</c> rule bound into the term — though
    /// the very same caller lists the scoped rows the term ranges over.
    /// </summary>
    /// <remarks>
    /// <c>depots</c> is spec §6.2's own example: <c>list</c> and <c>update</c> read <c>work_orders.exists(w, true)</c>, so the
    /// read path (the rendered <c>EXISTS</c>) and the write path (the relation probe) are each held. Its <c>get</c> is open,
    /// so a work order can name a depot at all, and so the person reading it by id is the control that the refused
    /// update — <c>404</c>, as every update an <c>update</c> rule filters out answers — is that rule's verdict, not a row
    /// the caller cannot see.
    /// </remarks>
    [Fact]
    public async Task A_global_entity_s_relation_into_a_scoped_entity_never_sees_a_tenant_role_on_every_engine()
    {
        await using var world = await TenantRolesWorldAsync();
        var depot = await SeedAsync(world, "depots", Region("D1"));
        await SeedAsync(world, "work_orders", WorkOrder(depot));

        using var workOrders = await SendAsPersonAsync(world, HttpMethod.Get, "/api/work_orders", _tenantDispatcher);
        using var listed = await SendAsPersonAsync(world, HttpMethod.Get, "/api/depots", _tenantDispatcher);
        using var read = await SendAsPersonAsync(world, HttpMethod.Get, $"/api/depots/{depot}", _tenantDispatcher);
        using var patched = await SendAsPersonAsync(
            world, HttpMethod.Patch, $"/api/depots/{depot}", _tenantDispatcher, new JsonObject { ["code"] = "TAKEN" });
        using var projectListed = await world.SendAsync(HttpMethod.Get, "/api/depots", _projectDispatcher);
        using var projectPatched = await world.SendAsync(
            HttpMethod.Patch, $"/api/depots/{depot}", _projectDispatcher, body: new JsonObject { ["code"] = "D2" });

        (await ItemsAsync(workOrders)).Count.ShouldBe(1, "the caller does see the work order the depot's rule ranges over");
        (await ItemsAsync(listed)).ShouldBeEmpty("a decision about a global entity never depends on a tenant role");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.ReadTextAsync());
        patched.StatusCode.ShouldBe(HttpStatusCode.NotFound, await patched.ReadTextAsync());
        (await world.CountRowsAsync("depots", "code", "TAKEN")).ShouldBe(0, "the refused update wrote nothing");
        (await ItemsAsync(projectListed)).Count.ShouldBe(1, "the same relation admits the role held as a project role");
        projectPatched.StatusCode.ShouldBe(HttpStatusCode.OK, await projectPatched.ReadTextAsync());
    }

    private Task<AlvoApiWorld> TenantRolesWorldAsync()
    {
        var people = new TestPersonResolver(
            (AcmeDispatcherToken, TestPersonResolver.Person("person:dispatcher", _acme, "dispatcher")),
            (AcmePlainToken, TestPersonResolver.Person("person:plain", _acme)));
        return AlvoApiWorld.FromDescriptorAsync(
            "tenant-roles-desk.alvo.json", [_projectDispatcher], new AlvoApiWorldSetup(ConfigureServices: people.Register), Engine);
    }

    private static Task<HttpResponseMessage> SendAsPersonAsync(
        AlvoApiWorld world, HttpMethod method, string path, KeyValuePair<string, string>[] person, JsonObject? body = null) =>
        world.SendAsync(method, path, body: body, headers: person);

    private static async Task<Guid> SeedAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var created = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _projectDispatcher, body: body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static async Task<JsonArray> ItemsAsync(HttpResponseMessage page)
    {
        page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.ReadTextAsync());
        return (await page.ReadJsonObjectAsync())["items"]!.AsArray();
    }

    private static JsonObject Region(string code) => new() { ["code"] = code };

    private static JsonObject WorkOrder(Guid? depot = null) =>
        Scoped(depot is { } id ? new JsonObject { ["title"] = "Boiler", ["depot_id"] = id } : new JsonObject { ["title"] = "Boiler" });

    private static JsonObject Scoped(JsonObject body)
    {
        body["tenant_id"] = _acme;
        return body;
    }

    private static KeyValuePair<string, string>[] Bearer(string token) => [new("Authorization", $"Bearer {token}")];
}
