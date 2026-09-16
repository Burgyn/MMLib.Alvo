using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Collections.Frozen;
using System.Data.Common;
using DescriptorFieldType = MMLib.Alvo.Descriptor.FieldType;
using SchemaFieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// One <see cref="EfAlvoData"/> over a real, in-memory SQLite database: the write path as a host reaches it,
/// with the hooks, the clock and the engine's own failures under the test's control.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the driver's own suite. <c>MMLib.Alvo.Data.Sqlite.Tests</c> boots a host through
/// <c>AddAlvo</c> and the migrator, which is what proves the wiring — and which also means a test there cannot
/// make one <c>INSERT</c> fail twice and then succeed, or watch the retry loop count its own attempts. This
/// assembles the same class from its nine constructor arguments instead, so the seam a write-path decision
/// actually turns on is a fake this file owns.
/// </para>
/// <para>
/// The database is created from the port's own read model (<c>EnsureCreated</c> over
/// <c>AlvoDataContext</c>), so the columns a write binds are the columns EF mapped rather than DDL a test
/// wrote out and could get wrong.
/// </para>
/// </remarks>
internal sealed class WritePathWorld : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private AlvoDataContextFactory _contexts = null!;

    private WritePathWorld(SqliteConnection connection, ServiceProvider services)
    {
        _connection = connection;
        _services = services;
    }

    /// <summary>The port under test.</summary>
    internal EfAlvoData Data { get; private set; } = null!;

    /// <summary>The hooks every write of this world runs; patches nothing until a test says so.</summary>
    internal StubBeforeHookRunner Hooks { get; } = new();

    /// <summary>The engine failures this world injects, and the statements it has seen.</summary>
    internal StatementFailureInterceptor Statements { get; } = new();

    /// <summary>The caller every write is performed as: a tenanted, authenticated identity.</summary>
    internal AlvoContext Caller { get; } = WritePathFixture.Caller();

    /// <summary>Boots a world over <paramref name="schema"/>, with <paramref name="descriptor"/>'s rules.</summary>
    /// <param name="descriptor">The descriptor whose rules the policy engine is primed with.</param>
    /// <param name="schema">The applied schema the read model and the write path are built from.</param>
    internal static async Task<WritePathWorld> StartAsync(AlvoDescriptor descriptor, SchemaModel schema)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var world = await BuildAsync(descriptor, schema, connection, services);

        return world;
    }

    private static async Task<WritePathWorld> BuildAsync(
        AlvoDescriptor descriptor, SchemaModel schema, SqliteConnection connection, ServiceProvider services)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, services.GetRequiredService<ICelCompiler>());
        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(descriptor.Name, catalog);

        var world = new WritePathWorld(connection, services);
        var contexts = new AlvoDataContextFactory(
            new FixedSchemaRegistry(schema),
            options => options
                .UseSqlite(connection, sqlite => sqlite.UseRelationalNulls())
                .AddInterceptors(world.Statements));

        await CreateTablesAsync(contexts);
        world._contexts = contexts;
        world.Data = Port(services, world, contexts);

        return world;
    }

    private static EfAlvoData Port(ServiceProvider services, WritePathWorld world, AlvoDataContextFactory contexts) =>
        new(
            services.GetRequiredService<IPolicyEngine>(),
            services.GetRequiredService<IPredicateEvaluator>(),
            world.Hooks,
            services.GetRequiredService<IPredicateRenderer>(),
            new TestFieldSqlRenderer(),
            new SqliteTestDialect(),
            contexts,
            TimeProvider.System,
            new AlvoOptions());

    private static async Task CreateTablesAsync(AlvoDataContextFactory contexts)
    {
        using var db = contexts.Create();
        await db.Database.EnsureCreatedAsync();
    }

    /// <summary>
    /// Writes one row through EF directly, <b>without</b> the port — so a test can start from a populated
    /// table while the port itself has still performed no write at all, which is what a once-per-process
    /// memo (the framework tables' own "already ensured" flags) is only observable from.
    /// </summary>
    /// <param name="title">The row's title.</param>
    internal async Task<Guid> SeedAsync(string title)
    {
        using var db = _contexts.Create();
        var id = Guid.NewGuid();
        db.Rows(WritePathFixture.Entity).Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["tenant_id"] = Caller.Tenant!.Value.Value,
            ["owner_id"] = Caller.User.Value,
            ["title"] = title,
        });
        await db.SaveChangesAsync();

        return id;
    }

    /// <summary>Runs one statement against this world's own connection, outside the port entirely.</summary>
    /// <param name="sql">The statement to run.</param>
    internal async Task<int> ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads one scalar out of this world's own connection, outside the port entirely.</summary>
    /// <param name="sql">The query to run.</param>
    internal async Task<object?> ScalarAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _services.DisposeAsync();
    }

    /// <summary>The applied schema, fixed for the life of one world.</summary>
    private sealed class FixedSchemaRegistry(SchemaModel schema) : ISchemaRegistry
    {
        public SchemaModel GetSchema() => schema;
    }
}

/// <summary>
/// The <c>before*</c> hooks of one world: nothing at all until a test sets <see cref="Patch"/>, and then
/// whatever that returns, on whichever operation <see cref="On"/> names.
/// </summary>
/// <remarks>
/// A fake rather than the real <c>BeforeHookRunner</c>, because the decisions under test are this port's
/// reaction to a patch — the key a hook moved, the field it rewrote — and reaching them through the compiler
/// would test the compiler instead.
/// </remarks>
internal sealed class StubBeforeHookRunner : IBeforeHookRunner
{
    /// <summary>The patch to answer with, or <see langword="null"/> to patch nothing.</summary>
    internal Func<AlvoRecord, IReadOnlyDictionary<string, object?>>? Patch { get; set; }

    /// <summary>The operation <see cref="Patch"/> fires on.</summary>
    internal DataOperation On { get; set; } = DataOperation.Create;

    /// <summary>How many times a hook has actually fired.</summary>
    internal int Runs { get; private set; }

    public IReadOnlyDictionary<string, object?> Run(
        string entity, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context,
        DateTimeOffset now)
    {
        if (Patch is not { } patch || operation != On)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        Runs++;

        return patch(candidate);
    }
}

/// <summary>
/// Records every statement one world issues, and fails the ones a test asked to fail — as the engine's own
/// <see cref="SqliteException"/>, so the port decides what it is rather than being told.
/// </summary>
/// <remarks>
/// The injected code is <c>SQLITE_BUSY</c> (5) and never <c>SQLITE_CONSTRAINT</c> (19), so
/// <see cref="SqliteTestDialect"/> decodes nothing and the failure reaches the port as the storage write
/// failure the retry loop is about, not as a constraint violation that leaves on the first attempt.
/// </remarks>
internal sealed class StatementFailureInterceptor : DbCommandInterceptor
{
    private readonly List<string> _seen = [];

    /// <summary>The substring a statement must contain to be failed, or <see langword="null"/> for none.</summary>
    internal string? FailWhenContains { get; set; }

    /// <summary>How many more matching statements to fail before letting them through.</summary>
    internal int FailuresLeft { get; set; }

    /// <summary>How many statements this interceptor has failed.</summary>
    internal int Failed { get; private set; }

    /// <summary>Every statement this world has issued, in order.</summary>
    internal IReadOnlyList<string> Seen => _seen;

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Observe(command.CommandText);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Observe(command.CommandText);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Observe(command.CommandText);

        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Observe(string sql)
    {
        _seen.Add(sql);
        if (FailWhenContains is not { } marker
            || FailuresLeft <= 0
            || !sql.Contains(marker, StringComparison.Ordinal))
        {
            return;
        }

        FailuresLeft--;
        Failed++;

        throw new SqliteException("SQLite Error 5: 'database is locked'.", 5, 5);
    }
}

/// <summary>
/// A SQLite-shaped <see cref="IAlvoSqlDialect"/> for this assembly: quoted identifiers, no row-lock clause
/// (SQLite takes none) and the engine's unique-violation decoding the driver itself performs.
/// </summary>
/// <remarks>
/// Not <see cref="TestSqlDialect"/>, whose <c>FOR TEST</c> lock clause is not SQL any engine would run, and
/// not the shipped <c>SqliteSqlDialect</c>, which lives in a package this assembly deliberately does not
/// reference — see the test project's own comments on why the production code here stays provider-agnostic.
/// </remarks>
internal sealed class SqliteTestDialect : IAlvoSqlDialect
{
    public string RowLockClause(PreImageMutation mutation) => string.Empty;

    public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return AlvoSqlIdentifier.Quote(entity.Name);
    }

    public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

    public string RenderNullProjection(string storeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);

        return $"CAST(NULL AS {storeType})";
    }

    public SqlConstraintViolation? DecodeConstraintViolation(DbException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is not SqliteException sqlite || sqlite.SqliteErrorCode != 19)
        {
            return null;
        }

        var columns = ViolatedColumns(sqlite.Message);

        return columns.Count == 0
            ? null
            : new SqlConstraintViolation { Kind = AlvoConstraintKind.Unique, Columns = columns };
    }

    private static IReadOnlyList<string> ViolatedColumns(string message)
    {
        const string prefix = "UNIQUE constraint failed: ";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        return [.. message[(start + prefix.Length)..]
            .TrimEnd('\'', '.', ' ')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unqualified)];
    }

    private static string Unqualified(string qualified) =>
        qualified.LastIndexOf('.') is var dot && dot < 0 ? qualified : qualified[(dot + 1)..];
}

/// <summary>
/// The one entity every write-path test in this assembly writes: audited, tenant-scoped, and carrying the
/// two caller-owned fields a rule can be written over.
/// </summary>
internal static class WritePathFixture
{
    /// <summary>The entity's name, in the schema and in the descriptor alike.</summary>
    internal const string Entity = "note";

    /// <summary>The applied schema one world is built from.</summary>
    /// <param name="audit">Whether the entity is audited — a non-audited one carries no version column.</param>
    internal static SchemaModel Schema(bool audit = true) => new([Note(audit)]);

    /// <summary>The fixture entity.</summary>
    /// <param name="audit">Whether the entity is audited.</param>
    internal static EntitySchema Note(bool audit = true) => new()
    {
        Name = Entity,
        Tenancy = TenancyMode.Scoped,
        Audit = audit,
        Fields = Fields(audit),
    };

    private static List<FieldSchema> Fields(bool audit)
    {
        var fields = new List<FieldSchema>(CallerFields());
        if (audit)
        {
            fields.AddRange(AuditFields());
        }

        return fields;
    }

    private static List<FieldSchema> CallerFields() =>
    [
        new FieldSchema { Name = "id", Type = SchemaFieldType.Uuid, Required = true },
        new FieldSchema { Name = "tenant_id", Type = SchemaFieldType.Uuid, Required = true, Indexed = true },
        new FieldSchema { Name = "owner_id", Type = SchemaFieldType.Uuid, Nullable = true },
        new FieldSchema { Name = "title", Type = SchemaFieldType.String, Nullable = true, MaxLength = 64 },
    ];

    private static List<FieldSchema> AuditFields() =>
    [
        new FieldSchema { Name = "created_at", Type = SchemaFieldType.DateTime, Nullable = true },
        new FieldSchema { Name = "created_by", Type = SchemaFieldType.Uuid, Nullable = true },
        new FieldSchema { Name = "updated_at", Type = SchemaFieldType.DateTime, Nullable = true },
        new FieldSchema { Name = "updated_by", Type = SchemaFieldType.Uuid, Nullable = true },
    ];

    /// <summary>A descriptor carrying whichever rules a test needs over the fixture entity.</summary>
    /// <param name="create">The <c>create</c> rule.</param>
    /// <param name="update">The <c>update</c> rule.</param>
    /// <param name="get">The <c>get</c> rule.</param>
    /// <param name="list">The <c>list</c> rule.</param>
    /// <param name="delete">The <c>delete</c> rule.</param>
    internal static AlvoDescriptor Descriptor(
        string? create = "true",
        string? update = "true",
        string? get = "true",
        string? list = "true",
        string? delete = "true") => new()
        {
            ApiVersion = "alvo.dev/v1",
            Name = "write-path-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Entity] = new EntityDescriptor
                {
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["title"] = new() { Type = DescriptorFieldType.String },
                        ["owner_id"] = new() { Type = DescriptorFieldType.Uuid },
                    },
                    Rules = new AccessRules
                    {
                        List = list,
                        Get = get,
                        Create = create,
                        Update = update,
                        Delete = delete,
                    },
                },
            },
        };

    /// <summary>The caller every write is performed as.</summary>
    internal static AlvoContext Caller() => new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a")),
        Roles = new HashSet<Role> { Role.Authenticated }.ToFrozenSet(),
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-00000000000b")),
    };

    /// <summary>
    /// A payload for <see cref="IAlvoData.CreateAsync"/>: the caller-owned fields plus the tenant the row is
    /// placed in, which is the one managed column a create legitimately carries.
    /// </summary>
    /// <param name="title">The row's title.</param>
    /// <param name="ownerId">The row's owner, defaulting to the fixture caller.</param>
    internal static Dictionary<string, object?> CreatePayload(string title, Guid? ownerId = null)
    {
        var payload = Payload(title, ownerId);
        payload["tenant_id"] = Caller().Tenant!.Value.Value;

        return payload;
    }

    /// <summary>A payload for the fixture entity.</summary>
    /// <param name="title">The row's title.</param>
    /// <param name="ownerId">The row's owner, defaulting to the fixture caller.</param>
    internal static Dictionary<string, object?> Payload(string title, Guid? ownerId = null) =>
        new(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["owner_id"] = ownerId ?? Caller().User.Value,
        };
}
