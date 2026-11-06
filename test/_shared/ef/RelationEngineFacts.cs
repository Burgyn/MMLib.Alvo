using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// Relation rules where the engine's own services matter, on whichever engine the deriving class supplies: a create
/// inside a transaction unit probes its relation inside the unit's transaction, so it sees a related row the unit itself
/// wrote and refuses one the caller may not list; and an entity whose name could spell a generated alias still
/// correlates its own row, never the related one.
/// </summary>
public abstract class RelationEngineFacts
{
    private static readonly AlvoContext _alice = Caller();

    private static readonly AlvoContext _bob = Caller();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Boots a host over <paramref name="schema"/> and <paramref name="descriptor"/> and answers its services.</summary>
    protected abstract Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor);

    [Fact]
    public async Task A_create_inside_a_unit_probes_its_relation_over_the_unit_s_own_rows()
    {
        var transactions = (await ServicesAsync(UnitsJson)).GetRequiredService<IAlvoTransactions>();

        var order = await transactions.TransactAsync(_alice, async (unit, ct) =>
        {
            var customer = await unit.CreateAsync("customers", Row(("owner_id", _alice.User.Value)), ct);
            return await unit.CreateAsync("orders", Row(("customer_id", customer["id"])), ct);
        }, cancellationToken: Ct);

        order["customer_id"].ShouldNotBeNull();
    }

    [Fact]
    public async Task A_create_inside_a_unit_is_refused_when_its_relation_fails()
    {
        var transactions = (await ServicesAsync(UnitsJson)).GetRequiredService<IAlvoTransactions>();
        var aliceCustomer = await transactions.TransactAsync(
            _alice, (unit, ct) => unit.CreateAsync("customers", Row(("owner_id", _alice.User.Value)), ct), cancellationToken: Ct);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => transactions.TransactAsync(
            _bob, (unit, ct) => unit.CreateAsync("orders", Row(("customer_id", aliceCustomer["id"])), ct), cancellationToken: Ct));
    }

    /// <summary>
    /// An entity named <c>alvo_r1</c> — what the first generated alias once was — reads its manager's region, not its own:
    /// a self-managed European row must not make a row under an American manager readable.
    /// </summary>
    [Fact]
    public async Task An_entity_named_like_a_relation_alias_still_correlates_its_own_row()
    {
        var data = (await ServicesAsync(AliasJson)).GetRequiredService<IAlvoData>();
        var european = await SeedAsync(data, null, "eu");
        await data.UpdateAsync("alvo_r1", european, Row(("manager_id", european)), _alice, cancellationToken: Ct);
        var american = await SeedAsync(data, null, "us");
        var underAmerica = await SeedAsync(data, american, "eu");

        (await data.GetAsync("alvo_r1", european, _alice, Ct)).ShouldNotBeNull();
        (await data.GetAsync("alvo_r1", underAmerica, _alice, Ct)).ShouldBeNull();
    }

    private static async Task<Guid> SeedAsync(IAlvoData data, Guid? manager, string region) =>
        (Guid)(await data.CreateAsync("alvo_r1", Row(("manager_id", manager), ("region", region)), _alice, cancellationToken: Ct))["id"]!;

    private async Task<IServiceProvider> ServicesAsync(string json)
    {
        var descriptor = AlvoDescriptor.Parse(json);
        return await StartAsync(DescriptorToSchemaMapper.Map(descriptor), descriptor);
    }

    private static AlvoContext Caller() => new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static Dictionary<string, object?> Row(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal);

    private const string UnitsJson = """
        {
          "apiVersion": "alvo.dev/v1", "name": "relation-units",
          "entities": {
            "customers": { "tenancy": "global", "fields": { "owner_id": { "type": "uuid" } },
              "rules": { "list": "owner_id == @user.id", "get": "owner_id == @user.id", "create": "owner_id == @user.id" } },
            "orders": { "tenancy": "global", "fields": { "customer_id": { "type": "ref", "entity": "customers" } },
              "rules": { "list": "customer_id.owner_id == @user.id", "get": "customer_id.owner_id == @user.id",
                         "create": "customer_id.owner_id == @user.id" } }
          }
        }
        """;

    private const string AliasJson = """
        {
          "apiVersion": "alvo.dev/v1", "name": "relation-alias",
          "entities": {
            "alvo_r1": { "tenancy": "global",
              "fields": { "manager_id": { "type": "ref", "entity": "alvo_r1" }, "region": { "type": "string" } },
              "rules": { "list": "true", "get": "manager_id.region == 'eu'", "create": "true", "update": "true" } }
          }
        }
        """;
}
