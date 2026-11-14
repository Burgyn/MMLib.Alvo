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
/// advisory lock in <c>pg_locks</c> — a condition, not a timing — and commits once the holder lets go. And why the locks
/// are needed at all: the crosswise pair of units below deadlocks without them (issue 478).
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

    /// <summary>
    /// Why D3 exists (issue 478). Two units of one room each hold an uncommitted booking when each writes a second booking that
    /// overlaps the other's first. Without the lock each second insert waits for the other unit — a cycle PostgreSQL breaks
    /// with an unmapped <c>40P01</c>. With it the second unit queues on the room before its first insert, the first unit
    /// commits, and the second's insert is checked against a committed row: one 409 exclusion, no deadlock. The barrier is a
    /// condition — the second unit has inserted, or is seen waiting on the room's lock — never a timing.
    /// </summary>
    [Fact]
    public async Task Two_units_booking_one_room_crosswise_queue_and_the_loser_is_refused_not_deadlocked()
    {
        await StartAsync();
        var units = _host!.Services.GetRequiredService<IAlvoTransactions>();
        var firstHolds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHolds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = OutcomeAsync(() => units.TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Bookings, Slot("r1", "09:00"), ct);
            firstHolds.SetResult();
            await HeldOrWaitingOnRoomAsync(secondHolds.Task, ct);
            await unit.CreateAsync(Bookings, Slot("r1", "11:30"), ct);
        }, cancellationToken: Ct));
        var second = OutcomeAsync(() => units.TransactAsync(_caller, async (unit, ct) =>
        {
            await firstHolds.Task;
            await unit.CreateAsync(Bookings, Slot("r1", "11:00"), ct);
            secondHolds.SetResult();
            await unit.CreateAsync(Bookings, Slot("r1", "09:30"), ct);
        }, cancellationToken: Ct));
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(outcome => outcome is null).ShouldBe(1);
        outcomes.OfType<Exception>().ShouldAllBe(failure => IsExclusion(failure));
    }

    private async Task<IAlvoData> StartAsync()
    {
        _host = await _fixture.StartAsync(Schema, Descriptor);
        return _host.Data;
    }

    private async Task ShouldWaitForTheLockAsync(Func<Task> write, Func<int, Task>? whileWaiting = null)
    {
        var key = RoomKey("r1");
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
        await using var probe = WaiterProbe(connection, key);
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

    private async Task HeldOrWaitingOnRoomAsync(Task held, CancellationToken unitDeadline)
    {
        await using var connection = _host!.Services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(unitDeadline);
        await using var probe = WaiterProbe(connection, RoomKey("r1"));
        while (!held.IsCompleted)
        {
            if (await probe.ExecuteScalarAsync(unitDeadline) is int)
            {
                return;
            }
        }
    }

    private static bool IsExclusion(Exception failure) =>
        failure is AlvoConstraintViolationException { Kind: AlvoConstraintKind.Exclusion };

    private static async Task<Exception?> OutcomeAsync(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return failure;
        }
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

    private static long RoomKey(string room) =>
        ExclusionLocks.KeyFor(Schema.Entities[0], Schema.Entities[0].Indexes[0], column => column switch
        {
            "tenant_id" => _tenant.Value,
            "room" => room,
            _ => null,
        });

    /// <summary>A probe answering the pid of a backend waiting — not yet granted — on the advisory lock <paramref name="key"/>.</summary>
    private static DbCommand WaiterProbe(DbConnection connection, long key)
    {
        var probe = connection.CreateCommand();
        probe.CommandText = """
            SELECT pid FROM pg_locks
            WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1
              AND classid::bigint = @high AND objid::bigint = @low
            """;
        probe.Parameters.Add(new NpgsqlParameter("high", (key >> 32) & 0xFFFFFFFF));
        probe.Parameters.Add(new NpgsqlParameter("low", key & 0xFFFFFFFF));
        return probe;
    }

    private static Guid Id(AlvoRecord record) => (Guid)record["id"]!;

    private static Dictionary<string, object?> Room(string room) => new(StringComparer.Ordinal) { ["room"] = room };

    /// <summary>A one-hour booking of <paramref name="room"/> from <paramref name="start"/> (<c>HH:mm</c>) on the test's day.</summary>
    private static Dictionary<string, object?> Slot(string room, string start)
    {
        var from = _nine.Date.Add(TimeSpan.Parse(start, System.Globalization.CultureInfo.InvariantCulture));
        var at = new DateTimeOffset(from, TimeSpan.Zero);
        return new(StringComparer.Ordinal)
        {
            ["tenant_id"] = _tenant.Value,
            ["room"] = room,
            ["starts_at"] = at,
            ["ends_at"] = at.AddHours(1),
        };
    }

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
        Tenancy = new MMLib.Alvo.Descriptor.Tenancy { Enabled = true },
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
