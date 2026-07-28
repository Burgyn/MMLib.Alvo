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
        var clock = new SteppingTimeProvider(Created, Updated);
        var host = await StartAsync(clock);
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
        var clock = new SteppingTimeProvider(Created, Updated);
        var host = await StartAsync(clock);
        var created = await host.Data.CreateAsync("invoices", Payload("first"), Caller, cancellationToken: Cancellation);

        var updated = await host.Data.UpdateAsync(
            "invoices", (Guid)created["id"]!, Payload("second"), Caller, cancellationToken: Cancellation);

        updated[AlvoManagedColumns.CreatedAt].ShouldBe(Created);
        updated[AlvoManagedColumns.UpdatedAt].ShouldBe(Updated);
    }

    /// <summary>
    /// The anonymous caller's all-zero <see cref="UserId"/> is reserved to mean "no identity", so the
    /// actor columns stay <see langword="null"/> rather than asserting that the anonymous caller authored
    /// the row — which would make it the recorded owner of every audited row it created.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_identity_leaves_the_actor_columns_null()
    {
        var host = await StartAsync(new SteppingTimeProvider(Created, Updated));

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
    /// A clock that answers each configured instant once and then repeats the last, so a create and the
    /// update that follows it are distinguishable without the test having to advance anything between them.
    /// </summary>
    private sealed class SteppingTimeProvider(params DateTimeOffset[] instants) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() =>
            instants[Math.Min(_reads++, instants.Length - 1)];
    }
}
