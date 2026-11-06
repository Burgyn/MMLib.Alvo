using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Npgsql;
using System.Data.Common;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// HX-12 D3, deterministically (review I2): every write path that an exclusion judges takes the resource's lock before it
/// writes. Another connection holds the key the write must take; the write is then seen <em>waiting</em> on exactly that
/// advisory lock in <c>pg_locks</c> — a condition, not a timing — and commits once the holder lets go.
/// </summary>
public sealed class PostgreSqlExclusionLockTests : IAsyncLifetime
{
    private const string Bookings = "bookings";

    private static readonly DateTimeOffset _nine = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId _tenant = TenantId.New();
    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = _tenant,
    };

    private readonly PostgreSqlAlvoDataFixture _fixture = new();
    private PostgreSqlAlvoDataHost? _host;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_create_waits_for_its_resource_s_lock()
    {
        var data = await StartAsync();

        await ShouldWaitForTheLockAsync(() => data.CreateAsync(Bookings, Booking("r1", 9), _caller, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_batch_create_waits_for_its_resource_s_lock()
    {
        var data = await StartAsync();

        await ShouldWaitForTheLockAsync(() => data.CreateManyAsync(Bookings, [Booking("r9", 9), Booking("r1", 9)], _caller, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_patch_waits_for_its_resource_s_lock()
    {
        var data = await StartAsync();
        var booking = await data.CreateAsync(Bookings, Booking("r2", 9), _caller, cancellationToken: Ct);

        await ShouldWaitForTheLockAsync(() => data.UpdateAsync(Bookings, Id(booking), Room("r1"), _caller, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_batch_update_waits_for_its_resource_s_lock()
    {
        var data = await StartAsync();
        var booking = await data.CreateAsync(Bookings, Booking("r2", 9), _caller, cancellationToken: Ct);

        await ShouldWaitForTheLockAsync(() => data.UpdateManyAsync(Bookings, [new AlvoRowPatch(Id(booking), Room("r1"))], _caller, cancellationToken: Ct));
    }

    /// <summary>
    /// A batch update takes every row's lock before it writes any row, in key order, so two batches cannot each hold one
    /// resource and wait for the other's: while it waits for the held key, it has not yet written to the table.
    /// </summary>
    [Fact]
    public async Task A_batch_update_takes_every_row_s_lock_before_it_writes_any()
    {
        var data = await StartAsync();
        var first = await data.CreateAsync(Bookings, Booking("r2", 9), _caller, cancellationToken: Ct);
        var second = await data.CreateAsync(Bookings, Booking("r3", 9), _caller, cancellationToken: Ct);

        await ShouldWaitForTheLockAsync(
            () => data.UpdateManyAsync(
                Bookings, [new AlvoRowPatch(Id(first), Room("r9")), new AlvoRowPatch(Id(second), Room("r1"))], _caller, cancellationToken: Ct),
            whileWaiting: async pid => (await HasWrittenAsync(pid)).ShouldBeFalse("the batch wrote a row before it held every row's lock"));
    }

    private async Task<IAlvoData> StartAsync()
    {
        _host = await _fixture.StartAsync(Schema, Descriptor);
        return _host.Data;
    }

    private async Task ShouldWaitForTheLockAsync(Func<Task> write, Func<int, Task>? whileWaiting = null)
    {
        var key = ExclusionLocks.KeyFor(Schema.Entities[0], Schema.Entities[0].Indexes[0], column => column switch
        {
            "tenant_id" => _tenant.Value,
            "room" => "r1",
            _ => null,
        });
        var holder = await HoldAsync(key);
        await using (holder.Connection)
        {
            var writing = Task.Run(write, Ct);
            var waiter = await WaiterOnAsync(key, writing);
            waiter.ShouldNotBeNull("the write never waited on its resource's lock");
            await (whileWaiting?.Invoke(waiter.Value) ?? Task.CompletedTask);
            await holder.Transaction.RollbackAsync(Ct);
            await writing;
        }
    }

    private async Task<(DbConnection Connection, DbTransaction Transaction)> HoldAsync(long key)
    {
        var connection = _host!.Services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var take = connection.CreateCommand();
        take.Transaction = transaction;
        take.CommandText = "SELECT pg_advisory_xact_lock(@key)";
        take.Parameters.Add(new NpgsqlParameter(nameof(key), key));
        await take.ExecuteNonQueryAsync(Ct);
        return (connection, transaction);
    }

    private async Task<int?> WaiterOnAsync(long key, Task writing)
    {
        await using var connection = _host!.Services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using var probe = connection.CreateCommand();
        probe.CommandText = """
            SELECT pid FROM pg_locks
            WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1
              AND classid::bigint = @high AND objid::bigint = @low
            """;
        probe.Parameters.Add(new NpgsqlParameter("high", (key >> 32) & 0xFFFFFFFF));
        probe.Parameters.Add(new NpgsqlParameter("low", key & 0xFFFFFFFF));
        while (!writing.IsCompleted)
        {
            if (await probe.ExecuteScalarAsync(Ct) is int pid)
            {
                return pid;
            }
        }

        await writing;
        return null;
    }

    private async Task<bool> HasWrittenAsync(int pid)
    {
        await using var connection = _host!.Services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using var probe = connection.CreateCommand();
        probe.CommandText = """
            SELECT COUNT(*) FROM pg_locks l JOIN pg_class c ON c.oid = l.relation
            WHERE l.pid = @pid AND c.relname = 'bookings' AND l.mode = 'RowExclusiveLock'
            """;
        probe.Parameters.Add(new NpgsqlParameter(nameof(pid), pid));
        return Convert.ToInt64(await probe.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static Guid Id(AlvoRecord record) => (Guid)record["id"]!;

    private static Dictionary<string, object?> Room(string room) => new(StringComparer.Ordinal) { ["room"] = room };

    private static Dictionary<string, object?> Booking(string room, int hour) => new(StringComparer.Ordinal)
    {
        ["tenant_id"] = _tenant.Value,
        ["room"] = room,
        ["starts_at"] = _nine.AddHours(hour - 9),
        ["ends_at"] = _nine.AddHours(hour - 8),
    };

    private static SchemaModel Schema => new([new EntitySchema
    {
        Name = Bookings,
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            new FieldSchema { Name = "room", Type = SchemaField.String, Required = true },
            new FieldSchema { Name = "starts_at", Type = SchemaField.DateTime, Required = true },
            new FieldSchema { Name = "ends_at", Type = SchemaField.DateTime, Required = true },
            new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true },
        ],
        Indexes = [new IndexSchema(["room"], Unique: false) { Exclude = new ExclusionSchema("starts_at", "ends_at") }],
    }]);

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "exclusion-locks",
        Tenancy = new Tenancy { Enabled = true },
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Bookings] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["room"] = new() { Type = DescField.String, Required = true },
                    ["starts_at"] = new() { Type = DescField.DateTime, Required = true },
                    ["ends_at"] = new() { Type = DescField.DateTime, Required = true },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };
}
