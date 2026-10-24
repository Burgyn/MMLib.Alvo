using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The data path's writes queue at the database's write gate too: one turn per write transaction, the outbox row it
/// appends included, one writer at a time, and a writer cancelled while it waits leaves nothing behind.
/// </summary>
public sealed class SqliteDataWriteGateTests : IAsyncDisposable
{
    private const string Notes = "notes";
    private const int Writers = 64;

    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(10);

    private static readonly AlvoContext _caller = new()
    {
        User = new UserId(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>Sixty-four data writers on real threads: every write takes one turn, and never two are inside at once.</summary>
    [Fact]
    public async Task Concurrent_data_writers_on_threads_queue_at_the_gate_one_at_a_time()
    {
        var (data, gate) = await StartAsync();

        var written = await Task.WhenAll(Enumerable.Range(0, Writers).Select(index => Task.Run(() => CreateAsync(data, $"note {index}"), Ct)));

        written.Distinct().Count().ShouldBe(Writers);
        gate.Entered.ShouldBe(Writers, "one turn per write transaction");
        gate.MostInside.ShouldBe(1);
    }

    /// <summary>
    /// A create and an update each append their outbox row in their own transaction, and each takes the turn once: the
    /// gate is not reentrant, so a second request for it from inside the write would wait for itself until the deadline.
    /// </summary>
    [Fact]
    public async Task A_data_write_and_the_outbox_row_it_appends_take_one_turn()
    {
        var (data, gate) = await StartAsync();

        var id = await CreateAsync(data, "first").WaitAsync(_deadline, Ct);
        gate.Entered.ShouldBe(1);
        await data.UpdateAsync(Notes, id, Values("second"), _caller, cancellationToken: Ct).WaitAsync(_deadline, Ct);

        gate.Entered.ShouldBe(2);
    }

    /// <summary>A data write cancelled while another holds the turn writes nothing, and the next write gets in at once.</summary>
    [Fact]
    public async Task A_data_write_cancelled_at_the_gate_writes_nothing_and_leaks_no_turn()
    {
        var (data, gate) = await StartAsync();
        var holder = await gate.Inner.EnterAsync(Ct);
        using var cancel = new CancellationTokenSource();
        var waiting = data.CreateAsync(Notes, Values("cancelled"), _caller, cancellationToken: cancel.Token);

        waiting.IsCompleted.ShouldBeFalse("the write is parked at the gate, not cancelled anywhere earlier");
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(waiting);
        holder.Dispose();
        await CreateAsync(data, "after").WaitAsync(_deadline, Ct);

        var page = await data.QueryAsync(new AlvoQuery { Entity = Notes }, _caller, Ct);
        page.Items.Select(note => note["title"]).ShouldBe(["after"]);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private async Task<(IAlvoData Data, RecordingWriteGate Gate)> StartAsync()
    {
        var gate = new RecordingWriteGate(new SerialRelationalWriteGate());
        var host = await _fixture.StartAsync(Schema, Descriptor, configure: services => services.AddSingleton<IRelationalWriteGate>(gate));

        return (host.Data, gate);
    }

    private static async Task<Guid> CreateAsync(IAlvoData data, string title)
    {
        var note = await data.CreateAsync(Notes, Values(title), _caller, cancellationToken: Ct);

        return (Guid)note[AlvoManagedColumns.Id]!;
    }

    private static Dictionary<string, object?> Values(string title) => new() { ["title"] = title };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "write-gate",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Notes] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) { ["title"] = new() { Type = DescField.String } },
                Rules = new AccessRules { List = Authenticated, Get = Authenticated, Create = Authenticated, Update = Authenticated },
            },
        },
    };

    private const string Authenticated = "'authenticated' in @user.roles";

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Notes,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true },
            ],
        },
    ]);
}
