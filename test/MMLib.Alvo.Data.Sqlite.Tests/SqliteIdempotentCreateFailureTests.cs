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
    /// The retry that turns a lost key race into a replay must not turn a duplicate <c>vin</c> into one — and
    /// since #138 it must not turn it into ten transactions either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the caller must never get is somebody's row</b>, and that has always been structural rather
    /// than a classification: an attempt answers as a replay <b>only</b> when the lookup finds a record for
    /// this key in this scope, and a duplicate <c>vin</c> commits no such record.
    /// </para>
    /// <para>
    /// <b>What changed is where it stops.</b> The retry catches any storage write failure, so a duplicate used
    /// to be re-attempted ten times — about 450 ms of transactions re-answering a question whose answer cannot
    /// change — and then surfaced as the port's invariant-violation family, which a request layer renders as a
    /// 500 with no field named. The entity's own insert now goes through the driver's dialect, which decodes
    /// the engine's constraint code; the refusal is <see cref="AlvoConstraintViolationException"/>, it is not a
    /// storage write failure, and it therefore leaves on the first attempt naming the field at fault.
    /// </para>
    /// <para>
    /// The provider's exception survives as the inner one, so a host's logging still has the engine's own
    /// diagnostics even though none of it reaches the caller.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_unique_violation_in_the_callers_own_data_is_a_conflict_naming_the_field()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var caller = Caller;
        var first = await host.Data.CreateAsync(Entity, Payload("VIN-1"), caller, NewToken(), Ct);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => host.Data.CreateAsync(
            Entity, Payload("VIN-1"), caller, NewToken(), Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Unique);
        refusal.Fields.ShouldBe(["vin"]);
        refusal.InnerException.ShouldNotBeNull("the provider's own failure must survive as the inner exception");
        refusal.Message.ShouldNotContain("VIN-1", Case.Sensitive, "the caller's own value is not echoed back");

        var all = await host.Data.QueryAsync(new AlvoQuery { Entity = Entity }, caller, Ct);
        all.Items.ShouldHaveSingleItem()["id"].ShouldBe((Guid)first["id"]!);
    }

    /// <summary>
    /// The retry itself is untouched: the <em>idempotency record's</em> own primary key is a unique constraint
    /// too, and translating that one would have turned the race this loop exists to converge on into a 409.
    /// </summary>
    /// <remarks>
    /// Asserted through the outcome rather than by counting attempts: the second call carries the <b>same</b>
    /// key and the same fingerprint, so it must answer as a replay of the first row — which is only reachable
    /// through the lookup the retry converges on. A build that translated the record's insert would answer
    /// <see cref="AlvoConstraintViolationException"/> here instead.
    /// </remarks>
    [Fact]
    public async Task A_replay_under_the_same_key_still_answers_the_first_row()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var caller = Caller;
        var token = NewToken();

        var first = await host.Data.CreateAsync(Entity, Payload("VIN-1"), caller, token, Ct);
        var replay = await host.Data.CreateAsync(Entity, Payload("VIN-1"), caller, token, Ct);

        replay["id"].ShouldBe(first["id"]);
        (await host.Data.QueryAsync(new AlvoQuery { Entity = Entity }, caller, Ct)).Items.ShouldHaveSingleItem();
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
