using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The two things the idempotent delete path owes that no other write path can prove for it: it is the one
/// verb whose replay <b>reads nothing</b>, so the recorded request is all it can compare against; and it has
/// to stand its own tables up, because it may be the first write a process ever performs.
/// </summary>
/// <remarks>
/// A delete's replay answers from the record alone — the row is gone by construction — so the fingerprint
/// check is the only thing between a key reused for a different request and a caller told a delete succeeded
/// that never happened. On an update the same reuse is caught a second time by the row that comes back; here
/// nothing comes back at all.
/// </remarks>
public sealed class SqliteAlvoDataIdempotentDeleteTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>
    /// A key reused for a <em>different</em> delete request is a conflict, not a replay — and the row the
    /// second request named survives.
    /// </summary>
    [Fact]
    public async Task One_key_reused_for_a_different_delete_is_a_conflict()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var first = await CreateAsync(host, "first");
        var second = await CreateAsync(host, "second");
        var key = $"key-{Guid.NewGuid():N}";

        await host.Data.DeleteAsync(
            Entity, first, Caller, idempotency: new AlvoIdempotency(key, "fingerprint-of-the-first"),
            cancellationToken: Ct);

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() => host.Data.DeleteAsync(
            Entity, second, Caller, idempotency: new AlvoIdempotency(key, "fingerprint-of-the-second"),
            cancellationToken: Ct));

        (await host.Data.GetAsync(Entity, second, Caller, Ct)).ShouldNotBeNull(
            "the refused request must not have deleted");
    }

    /// <summary>
    /// An idempotent delete that is the <b>first</b> write of a process stands the outbox up for itself and
    /// queues its event, rather than failing on a table nothing has created yet.
    /// </summary>
    /// <remarks>
    /// The row is seeded out of band precisely so no earlier write has already created the table or set the
    /// port's own "already ensured" flag — which is what makes this reachable at all, and why no other fact
    /// here can see it.
    /// </remarks>
    [Fact]
    public async Task An_idempotent_delete_that_is_the_first_write_stands_the_outbox_up_for_itself()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var seeded = Guid.NewGuid();
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(),
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal)
            {
                [Entity] = [new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = seeded, ["title"] = "seeded",
                })],
            },
            Ct);

        await host.Data.DeleteAsync(
            Entity, seeded, Caller, idempotency: new AlvoIdempotency($"key-{Guid.NewGuid():N}", "digest"),
            cancellationToken: Ct);

        var world = new AlvoDataOutboxWorld(host.Data, host.Services);
        (await world.EventsAsync()).ShouldHaveSingleItem().Type.ShouldBe($"entity.{Entity}.deleted");
    }

    private static async Task<Guid> CreateAsync(AlvoDataHost host, string title) =>
        (Guid)(await host.Data.CreateAsync(
            Entity,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = title },
            Caller,
            cancellationToken: Ct))["id"]!;

    private const string Entity = "notes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoContext Caller { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "idempotent-delete-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String },
                },
                Rules = new AccessRules
                {
                    List = "true",
                    Get = "true",
                    Create = "true",
                    Update = "true",
                    Delete = "true",
                },
            },
        },
    };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true, MaxLength = 100 },
            ],
        },
    ]);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
