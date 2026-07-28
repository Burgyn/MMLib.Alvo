using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// What an idempotent create does when the write fails for a reason that is <b>not</b> a race on the key.
/// Engine-specific by necessity: it needs a real unique constraint in the caller's own data, which the
/// in-memory reference has no way to declare, so this cannot live on the inherited suite.
/// </summary>
public sealed class SqliteIdempotentCreateFailureTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>
    /// The retry that turns a lost key race into a replay must not turn a duplicate <c>vin</c> into one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retry catches any storage write failure, because neither engine exposes which constraint refused
    /// the write without reading a provider error code — which this package deliberately does not do (see
    /// <c>VersionRowWriter</c>). So the narrowness has to come from the shape instead: an attempt answers as a
    /// replay <b>only</b> when the lookup finds a record for this key in this scope, and a duplicate <c>vin</c>
    /// commits no such record. Every attempt therefore takes the insert path again and fails again.
    /// </para>
    /// <para>
    /// What the caller must never get is <em>somebody's row</em>. What they do get is the port's own
    /// invariant-violation family with the provider's exception inside it, rather than the raw
    /// <c>DbUpdateException</c> that used to escape the five families <c>IAlvoData</c> promises — PR3's
    /// problem-details layer has nothing but the type to map a status from.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_unique_violation_in_the_callers_own_data_is_not_mistaken_for_a_replay()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var caller = Caller;
        var first = await host.Data.CreateAsync(Entity, Payload("VIN-1"), caller, NewToken(), Ct);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => host.Data.CreateAsync(
            Entity, Payload("VIN-1"), caller, NewToken(), Ct));

        refusal.InnerException.ShouldNotBeNull("the provider's own failure must survive as the inner exception");
        refusal.Message.ShouldContain("idempotency");
        refusal.ShouldNotBeOfType<AlvoIdempotencyConflictException>();

        var all = await host.Data.QueryAsync(new AlvoQuery { Entity = Entity }, caller, Ct);
        all.Items.ShouldHaveSingleItem()["id"].ShouldBe((Guid)first["id"]!);
    }

    /// <summary>
    /// The counterweight: the same entity accepts a second, non-colliding row under its own key, so the
    /// refusal above cannot be satisfied by an implementation whose idempotent create never succeeds twice.
    /// </summary>
    [Fact]
    public async Task A_second_idempotent_create_that_violates_nothing_still_lands()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var caller = Caller;

        await host.Data.CreateAsync(Entity, Payload("VIN-1"), caller, NewToken(), Ct);
        await host.Data.CreateAsync(Entity, Payload("VIN-2"), caller, NewToken(), Ct);

        var all = await host.Data.QueryAsync(new AlvoQuery { Entity = Entity }, caller, Ct);
        all.Items.Count.ShouldBe(2);
    }

    private const string Entity = "vehicles";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh key each time, so nothing here is a replay — the failure must come from the row itself.</summary>
    private static AlvoIdempotency NewToken() => new($"key-{Guid.NewGuid():N}", $"{Entity}:digest");

    private static Dictionary<string, object?> Payload(string vin) =>
        new(StringComparer.Ordinal) { ["vin"] = vin };

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "idempotent-create-failure",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new EntityDescriptor
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["vin"] = new() { Type = DescField.String, Required = true },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
            },
        },
    };

    /// <summary>The same entity with a <b>unique</b> index on <c>vin</c> — the constraint the fact needs.</summary>
    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "vin", Type = SchemaField.String, Required = true, MaxLength = 32 },
            ],
            Indexes = [new IndexSchema(["vin"], true)],
        },
    ]);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
