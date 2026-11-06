using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The audit stamp against a real engine and a <b>fixed</b> clock. The inherited suite proves the columns
/// are populated and by whom; these facts pin the exact instant, which is only assertable because the
/// stamp reads an injected <see cref="TimeProvider"/> rather than <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class SqliteAlvoDataAuditTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    private static DateTimeOffset Created => new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static DateTimeOffset Updated => new(2026, 9, 10, 11, 12, 13, TimeSpan.Zero);

    [Fact]
    public async Task A_create_stamps_all_four_audit_columns_from_the_injected_clock()
    {
        var host = await StartAsync(new SettableTimeProvider(Created));
        var caller = Caller;

        var created = await host.Data.CreateAsync("invoices", Payload("first"), caller, cancellationToken: Cancellation);

        created[AlvoManagedColumns.CreatedAt].ShouldBe(Created);
        created[AlvoManagedColumns.UpdatedAt].ShouldBe(Created);
        created[AlvoManagedColumns.CreatedBy].ShouldBe(caller.User.Value);
        created[AlvoManagedColumns.UpdatedBy].ShouldBe(caller.User.Value);
    }

    [Fact]
    public async Task An_update_advances_only_the_updated_columns()
    {
        var clock = new SettableTimeProvider(Created);
        var host = await StartAsync(clock);
        var created = await host.Data.CreateAsync("invoices", Payload("first"), Caller, cancellationToken: Cancellation);
        clock.Now = Updated;

        var updated = await host.Data.UpdateAsync(
            "invoices", (Guid)created["id"]!, Payload("second"), Caller, cancellationToken: Cancellation);

        updated[AlvoManagedColumns.CreatedAt].ShouldBe(Created);
        updated[AlvoManagedColumns.UpdatedAt].ShouldBe(Updated);
    }

    /// <summary>
    /// A batch update stamps exactly what a single update does — <c>updated_at</c> from the injected clock and
    /// <c>updated_by</c> from the caller — and leaves the creation record alone (issue #349).
    /// </summary>
    [Fact]
    public async Task A_batch_update_advances_only_the_updated_columns()
    {
        var clock = new SettableTimeProvider(Created);
        var host = await StartAsync(clock);
        var creator = Caller;
        var updater = Caller;
        var created = await host.Data.CreateAsync("invoices", Payload("first"), creator, cancellationToken: Cancellation);
        clock.Now = Updated;

        var batch = await host.Data.UpdateManyAsync(
            "invoices", [new AlvoRowPatch((Guid)created["id"]!, Payload("second"))], updater,
            cancellationToken: Cancellation);

        var updated = batch.Rows.ShouldHaveSingleItem();
        updated[AlvoManagedColumns.CreatedAt].ShouldBe(Created);
        updated[AlvoManagedColumns.CreatedBy].ShouldBe(creator.User.Value);
        updated[AlvoManagedColumns.UpdatedAt].ShouldBe(Updated);
        updated[AlvoManagedColumns.UpdatedBy].ShouldBe(updater.User.Value);
    }

    /// <summary>A batch create stamps all four audit columns from the one instant the batch is written at.</summary>
    [Fact]
    public async Task A_batch_create_stamps_all_four_audit_columns_from_the_injected_clock()
    {
        var host = await StartAsync(new SettableTimeProvider(Created));
        var caller = Caller;

        var batch = await host.Data.CreateManyAsync(
            "invoices", [Payload("a"), Payload("b")], caller, cancellationToken: Cancellation);

        batch.Rows.Count.ShouldBe(2);
        foreach (var row in batch.Rows)
        {
            row[AlvoManagedColumns.CreatedAt].ShouldBe(Created);
            row[AlvoManagedColumns.UpdatedAt].ShouldBe(Created);
            row[AlvoManagedColumns.CreatedBy].ShouldBe(caller.User.Value);
            row[AlvoManagedColumns.UpdatedBy].ShouldBe(caller.User.Value);
        }
    }

    /// <summary>
    /// The anonymous caller's all-zero <see cref="UserId"/> is reserved to mean "no identity", so the
    /// actor columns stay <see langword="null"/> rather than asserting that the anonymous caller authored
    /// the row — which would make it the recorded owner of every audited row it created.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_identity_leaves_the_actor_columns_null()
    {
        var host = await StartAsync(new SettableTimeProvider(Created));

        var created = await host.Data.CreateAsync("invoices", Payload("anon"), AlvoContext.Anonymous, cancellationToken: Cancellation);

        created[AlvoManagedColumns.CreatedBy].ShouldBeNull();
        created[AlvoManagedColumns.UpdatedBy].ShouldBeNull();
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static Dictionary<string, object?> Payload(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title };

    private Task<AlvoDataHost> StartAsync(TimeProvider time) => _fixture.StartAsync(Schema, Descriptor, time);

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "audit-fixture",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["invoices"] = new EntityDescriptor
            {
                Tenancy = EntityTenancy.Global,
                Audit = true,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true" },
            },
        },
    };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = "invoices",
            Tenancy = TenancyMode.Global,
            Audit = true,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.CreatedAt, Type = SchemaField.DateTime, Required = true },
                new FieldSchema { Name = AlvoManagedColumns.CreatedBy, Type = SchemaField.Uuid, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedAt, Type = SchemaField.DateTime, Required = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedBy, Type = SchemaField.Uuid, Nullable = true },
            ],
        },
    ]);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// A clock that answers <see cref="Now"/> however often it is read: a data-port call reads it for the flow's
    /// instant as well as for each write's stamp (HX-11 Rulings 1, 2), so the facts set it between calls rather than
    /// count reads.
    /// </summary>
    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
