using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;

namespace MMLib.Alvo.Data.Spike;

/// <summary>The eight pass/fail questions, replayed per engine. Every answer is printed, never asserted.</summary>
public sealed class Probes(SpikeEngine engine, ICelCompiler compiler, IPredicateRenderer renderer)
{
    private readonly SqlCapture _capture = new();

    private static readonly AlvoContext _alice = new()
    {
        User = new UserId(Fixture.AliceId),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = new TenantId(Fixture.AcmeTenant),
    };

    private IRelationalTypeMappingSource _mappings = null!;
    private DbConnection _binderConnection = null!;

    public async Task RunAsync()
    {
        Header($"ENGINE: {engine.Name}");
        await Fixture.CreateAndSeedAsync(engine);

        _binderConnection = await OpenAsync();
        await using (var bootstrap = NewContext(_binderConnection))
        {
            _mappings = bootstrap.GetService<IRelationalTypeMappingSource>();
        }

        _capture.Clear();

        await Q0_ModelShape();
        await Q1_RawPredicateOverPropertyBag();
        await Q2_PredicateInWhere();
        await Q3_ComposedWithMoreClauses();
        await Q3cd_SortAndKeyset();
        await Q4_Projection();
        await Q5_Writes();
        await Q6_Parameters();
        await Q7_OneTransaction();
        await Q8_IdentifierQuoting();
        await X1_MalformedQueryExceptionShape();
        await X2_DynamicEntityJsonPath();
    }

    // ---------------------------------------------------------------- helpers

    private static void Header(string text)
    {
        Console.WriteLine();
        Console.WriteLine("################################################################");
        Console.WriteLine("# " + text);
        Console.WriteLine("################################################################");
    }

    private static void Section(string text)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + text + " " + new string('=', Math.Max(0, 60 - text.Length)));
    }

    private static void Note(string text) => Console.WriteLine("  " + text);

    private static void Fail(Exception exception)
    {
        Console.WriteLine($"  !! {exception.GetType().FullName}");
        foreach (var line in (exception.Message ?? string.Empty).Split('\n'))
        {
            Console.WriteLine("     " + line.TrimEnd());
        }

        if (exception.InnerException is { } inner)
        {
            Console.WriteLine($"     inner: {inner.GetType().Name}: {inner.Message.Split('\n')[0]}");
        }
    }

    private void DumpCapture()
    {
        foreach (var entry in _capture.Log)
        {
            Console.Write(entry);
        }

        _capture.Clear();
    }

    private SpikeContext NewContext(DbConnection connection, bool allOptional = false)
    {
        var options = new DbContextOptionsBuilder();
        engine.UseProvider(options, connection);
        options.AddInterceptors(_capture);
        options.ReplaceService<IModelCacheKeyFactory, SpikeModelCacheKeyFactory>();
        return new SpikeContext(options.Options, Fixture.Model, engine.Schema, allOptional);
    }

    private async Task<DbConnection> OpenAsync()
    {
        var connection = engine.CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Compiles + renders a real policy predicate through PR1's compiler and renderer.</summary>
    private SqlPredicate Render(string cel, string prefix)
    {
        var compiled = compiler.Compile(cel, CelProfile.Rule, Fixture.Entity);
        if (!compiled.IsSuccess)
        {
            throw new InvalidOperationException(
                "CEL did not compile: " + string.Join("; ", compiled.Errors.Select(e => e.Message)));
        }

        return renderer.Render(compiled.Expression!, _alice, engine.Fields, prefix);
    }

    /// <summary>
    /// Binds a <see cref="SqlPredicate"/>'s parameter bag through <b>EF's own relational type
    /// mapping</b> — the only binding that is guaranteed to agree with how EF wrote the column.
    /// See <see cref="ToParametersHandBound"/> for what happens when you don't.
    /// </summary>
    private DbParameter[] ToParameters(params SqlPredicate[] predicates)
    {
        using var command = _binderConnection.CreateCommand();
        return
        [
            .. predicates
                .SelectMany(p => p.Parameters)
                .Select(kv =>
                {
                    var value = kv.Value;
                    var mapping = value is null ? null : _mappings.FindMapping(value.GetType());
                    return mapping is null
                        ? engine.CreateParameter("@" + kv.Key, value)
                        : mapping.CreateParameter(command, "@" + kv.Key, value, nullable: true);
                }),
        ];
    }

    /// <summary>The naive binding: hand the raw CLR value to the provider's own parameter type.</summary>
    private DbParameter[] ToParametersHandBound(params SqlPredicate[] predicates) =>
        [.. predicates.SelectMany(p => p.Parameters).Select(kv => engine.CreateParameter("@" + kv.Key, kv.Value))];

    private DbParameter Bound(string name, object? value)
    {
        using var command = _binderConnection.CreateCommand();
        var mapping = value is null ? null : _mappings.FindMapping(value.GetType());
        return mapping is null
            ? engine.CreateParameter(name, value)
            : mapping.CreateParameter(command, name, value, nullable: true);
    }

    private static string Row(Dictionary<string, object> row) =>
        "{ " + string.Join(", ", row.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}")) + " }";

    private string Table() => Fixture.QualifiedTable(engine);

    // ---------------------------------------------------------------- Q0

    private async Task Q0_ModelShape()
    {
        Section("Q0  is DescriptorModelBuilder's `builder.Entity(name)` already a property bag?");

        var modelBuilder = engine.NewModelBuilder();
        var entityBuilder = modelBuilder.Entity(Fixture.EntityName);
        entityBuilder.Property(typeof(Guid), "id");
        entityBuilder.HasKey("id");
        var model = modelBuilder.FinalizeModel();
        var entityType = model.FindEntityType(Fixture.EntityName)!;
        Note($"ModelBuilder.Entity(string).ClrType   = {entityType.ClrType.FullName}");
        Note($"  IsPropertyBag                      = {entityType.IsPropertyBag}");
        Note($"  HasSharedClrType                   = {entityType.HasSharedClrType}");
        Note($"  id is an indexer property          = {entityType.FindProperty("id")!.IsIndexerProperty()}");

        // And what the spike's own SpikeContext (SharedTypeEntity<Dictionary<string,object>>) produces.
        await using var connection = await OpenAsync();
        await using var context = NewContext(connection);
        var contextEntity = context.Model.FindEntityType(Fixture.EntityName)!;
        Note($"SharedTypeEntity<Dictionary<,>>      = {contextEntity.ClrType.FullName}, IsPropertyBag={contextEntity.IsPropertyBag}");
        Note($"  table                              = {contextEntity.GetSchema() ?? "(default)"}.{contextEntity.GetTableName()}");
    }

    // ---------------------------------------------------------------- Q1 / Q2

    private async Task Q1_RawPredicateOverPropertyBag()
    {
        Section("Q1  raw WHERE fragment + named parameters over a property bag, read back as dictionaries");

        var policy = Render("owner_id == @user.id", "alvo_p");
        var tenant = Render("tenant_id == @tenant.id", "alvo_t");
        Note($"policy predicate  : {policy.Sql}");
        Note($"policy parameters : {string.Join(", ", policy.Parameters.Select(kv => $"{kv.Key}={kv.Value}"))}");
        Note($"tenant predicate  : {tenant.Sql}");
        Note($"tenant parameters : {string.Join(", ", tenant.Parameters.Select(kv => $"{kv.Key}={kv.Value}"))}");

        var sql = $"SELECT * FROM {Table()} WHERE ({policy.Sql}) AND ({tenant.Sql})";
        Note($"raw SQL passed to FromSqlRaw: {sql}");

        await using var connection = await OpenAsync();
        await using var context = NewContext(connection);
        try
        {
            var rows = await context.Rows(Fixture.EntityName)
                .FromSqlRaw(sql, ToParameters(policy, tenant))
                .AsNoTracking()
                .ToListAsync();

            DumpCapture();
            Note($"rows returned: {rows.Count}");
            foreach (var row in rows)
            {
                Note("  " + Row(row));
            }

            Note($"CLR types: {string.Join(", ", rows[0].Where(kv => kv.Value is not null).Select(kv => $"{kv.Key}:{kv.Value.GetType().Name}"))}");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }
    }

    private async Task Q2_PredicateInWhere()
    {
        Section("Q2  is the predicate in the WHERE clause of ONE statement (not a post-filter)?");

        var policy = Render("owner_id == @user.id", "alvo_p");
        var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql}";

        await using var connection = await OpenAsync();
        await using var context = NewContext(connection);

        var rows = await context.Rows(Fixture.EntityName).FromSqlRaw(sql, ToParameters(policy)).AsNoTracking().ToListAsync();
        var executed = _capture.Log.Count;
        DumpCapture();
        Note($"statements executed: {executed}");
        Note($"rows returned      : {rows.Count} (of 4 seeded; Bob's row and no other owner's row must be absent)");
        Note($"plates             : {string.Join(", ", rows.Select(r => r["plate"]))}");
        Note("(alice owns ACME-001, ACME-002 and OTHR-001; bob owns ACME-003)");
    }

    // ---------------------------------------------------------------- Q3

    private async Task Q3_ComposedWithMoreClauses()
    {
        Section("Q3  policy predicate AND caller filter AND ORDER BY AND limit, in one statement");

        var policy = Render("owner_id == @user.id", "alvo_p");
        var tenant = Render("tenant_id == @tenant.id", "alvo_t");
        var sql = $"SELECT * FROM {Table()} WHERE ({policy.Sql}) AND ({tenant.Sql})";

        await using var connection = await OpenAsync();
        await using var context = NewContext(connection);
        try
        {
            var rows = await context.Rows(Fixture.EntityName)
                .FromSqlRaw(sql, ToParameters(policy, tenant))
                .Where(e => EF.Property<string>(e, "status") == "open")
                .OrderByDescending(e => EF.Property<string>(e, "plate"))
                .Take(5)
                .AsNoTracking()
                .ToListAsync();

            DumpCapture();
            Note($"statements: 1 expected; rows: {rows.Count}; plates: {string.Join(", ", rows.Select(r => r["plate"]))}");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }

        Section("Q3b  same, but the caller filter names a parameter EF captures from a closure");
        var callerStatus = "open";
        await using var context2 = NewContext(connection);
        try
        {
            var rows = await context2.Rows(Fixture.EntityName)
                .FromSqlRaw(sql, ToParameters(policy, tenant))
                .Where(e => EF.Property<string>(e, "status") == callerStatus)
                .OrderBy(e => EF.Property<long?>(e, "mileage"))
                .Take(2)
                .AsNoTracking()
                .ToListAsync();

            DumpCapture();
            Note($"rows: {rows.Count}; plates: {string.Join(", ", rows.Select(r => r["plate"]))}");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }
    }

    private async Task Q3cd_SortAndKeyset()
    {
        Section("Q3c  AlvoSort.Nulls — explicit NULL placement, emulated in LINQ over a property bag");

        var policy = Render("owner_id == @user.id", "alvo_p");
        var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql}";
        await using var connection = await OpenAsync();
        await using (var context = NewContext(connection))
        {
            try
            {
                var rows = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, ToParameters(policy))
                    // NullPlacement.First: nulls sort before every non-null value.
                    .OrderBy(e => EF.Property<string>(e, "status") == null ? 0 : 1)
                    .ThenBy(e => EF.Property<string>(e, "status"))
                    .AsNoTracking()
                    .ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count} — note whether the engine emitted CASE WHEN or a native NULLS clause");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q3d  keyset cursor — a composite (plate, id) > (@plate, @id) predicate in LINQ");
        var afterPlate = "ACME-001";
        var afterId = Fixture.AliceCar;
        await using (var context = NewContext(connection))
        {
            try
            {
                var rows = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, ToParameters(policy))
                    .Where(e => string.Compare(EF.Property<string>(e, "plate"), afterPlate) > 0
                        || (EF.Property<string>(e, "plate") == afterPlate && EF.Property<Guid>(e, "id").CompareTo(afterId) > 0))
                    .OrderBy(e => EF.Property<string>(e, "plate"))
                    .ThenBy(e => EF.Property<Guid>(e, "id"))
                    .AsNoTracking()
                    .ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count}; plates: {string.Join(", ", rows.Select(r => r["plate"]))}");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }
    }

    // ---------------------------------------------------------------- Q4

    private async Task Q4_Projection()
    {
        Section("Q4a  FromSqlRaw whose SELECT list omits the hidden field");

        var policy = Render("owner_id == @user.id", "alvo_p");
        var visible = Fixture.Entity.Fields.Select(f => f.Name).Where(n => n != Fixture.HiddenField).ToList();
        var columns = string.Join(", ", visible.Select(Fixture.Quote));
        var sql = $"SELECT {columns} FROM {Table()} WHERE {policy.Sql}";
        Note($"raw SQL: {sql}");

        await using var connection = await OpenAsync();
        await using var context = NewContext(connection);
        try
        {
            var rows = await context.Rows(Fixture.EntityName).FromSqlRaw(sql, ToParameters(policy)).AsNoTracking().ToListAsync();
            DumpCapture();
            Note($"rows: {rows.Count}; first row keys: {string.Join(",", rows[0].Keys.Order(StringComparer.Ordinal))}");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }

        Section("Q4b  dynamic Select over the FromSql root — does EF restrict the outer SELECT list?");
        await using var context2 = NewContext(connection);
        try
        {
            var query = context2.Rows(Fixture.EntityName).FromSqlRaw(sql.Replace($"SELECT {columns}", "SELECT *", StringComparison.Ordinal), ToParameters(policy));
            var rows = await ProjectToArray(query, visible).ToListAsync();
            DumpCapture();
            Note($"rows: {rows.Count}; first: [{string.Join(", ", rows[0].Select(v => v ?? "NULL"))}]");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }

        Section("Q4c  dynamic Select over the plain DbSet (no FromSql) — the SELECT list EF itself builds");
        await using var context3 = NewContext(connection);
        try
        {
            var rows = await ProjectToArray(context3.Rows(Fixture.EntityName), visible).ToListAsync();
            DumpCapture();
            Note($"rows: {rows.Count}");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }

        Section("Q4e  FromSqlRaw that projects NULL in place of the hidden column (keeps EF happy, keeps the value in the table)");
        var nulled = string.Join(", ", Fixture.Entity.Fields.Select(f =>
            f.Name == Fixture.HiddenField
                ? $"CAST(NULL AS {engine.ColumnType(f.Type)}) AS {Fixture.Quote(f.Name)}"
                : Fixture.Quote(f.Name)));
        var nulledSql = $"SELECT {nulled} FROM {Table()} WHERE {policy.Sql}";
        Note($"raw SQL: {nulledSql}");
        await using (var context4 = NewContext(connection))
        {
            try
            {
                var rows = await context4.Rows(Fixture.EntityName).FromSqlRaw(nulledSql, ToParameters(policy)).AsNoTracking().ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count}; {Fixture.HiddenField} = {rows[0][Fixture.HiddenField] ?? "NULL"}");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q4f  same trick on a NON-NULLABLE column (a hidden required field) — does EF reject the NULL?");
        var nulledRequired = string.Join(", ", Fixture.Entity.Fields.Select(f =>
            f.Name == "plate"
                ? $"CAST(NULL AS {engine.ColumnType(f.Type)}) AS {Fixture.Quote(f.Name)}"
                : Fixture.Quote(f.Name)));
        await using (var context5 = NewContext(connection))
        {
            try
            {
                var rows = await context5.Rows(Fixture.EntityName)
                    .FromSqlRaw($"SELECT {nulledRequired} FROM {Table()} WHERE {policy.Sql}", ToParameters(policy))
                    .AsNoTracking().ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count}; plate = {rows[0]["plate"] ?? "NULL"} — EF accepted a NULL in a required property");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q4g  Q4f again, but with EVERY property optional in the runtime model");
        await using (var optional = NewContext(connection, allOptional: true))
        {
            try
            {
                var rows = await optional.Rows(Fixture.EntityName)
                    .FromSqlRaw($"SELECT {nulledRequired} FROM {Table()} WHERE {policy.Sql}", ToParameters(policy))
                    .AsNoTracking().ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count}; plate = {rows[0]["plate"] ?? "NULL"}; status = {rows[0]["status"] ?? "NULL"}");
                Note("=> a hidden NOT NULL column can be NULL-projected when the runtime model marks it optional");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q4h  can that same all-optional model still INSERT and honour the DB's NOT NULL?");
        await using (var optional = NewContext(connection, allOptional: true))
        {
            try
            {
                optional.Rows(Fixture.EntityName).Add(new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid(),
                    ["tenant_id"] = Fixture.AcmeTenant,
                    ["owner_id"] = Fixture.AliceId,
                    ["plate"] = null!,     // required in the DB, optional in the runtime model
                    ["status"] = "open",
                    ["secret_note"] = null!,
                    ["mileage"] = 1L,
                    ["price"] = 1m,
                    ["is_active"] = true,
                    ["created_at"] = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
                });
                await optional.SaveChangesAsync();
                DumpCapture();
                Note("!! the INSERT succeeded — the DB's NOT NULL did NOT stop it");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Note("the DB rejected the NULL (this is the behaviour PR2 wants):");
                Fail(exception);
            }
        }

        Section("Q4d  hand-built ADO.NET SELECT with an explicit column list (option b)");
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in ToParameters(policy))
            {
                command.Parameters.Add(parameter);
            }

            Console.Write(SqlCapture.Describe(command));
            await using var reader = await command.ExecuteReaderAsync();
            var count = 0;
            var first = new Dictionary<string, object>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                if (count++ == 0)
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        first[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                    }
                }
            }

            Note($"rows: {count}; columns in result set: {string.Join(",", first.Keys)}");
            Note($"CLR types: {string.Join(", ", first.Where(kv => kv.Value is not null).Select(kv => $"{kv.Key}:{kv.Value.GetType().Name}"))}");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>
    /// Builds the <c>ExecuteUpdate</c> setter list at runtime from a field/value patch — EF Core 10's
    /// non-expression <c>Action&lt;UpdateSettersBuilder&lt;T&gt;&gt;</c> overload, reached by reflection
    /// because the property type varies per field.
    /// </summary>
    private static Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Dictionary<string, object>>> BuildSetters(
        IReadOnlyDictionary<string, object?> patch)
    {
        var setProperty = typeof(Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Dictionary<string, object>>)
            .GetMethods()
            .Single(m => m.Name == "SetProperty"
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType.IsGenericMethodParameter);

        return builder =>
        {
            foreach (var (name, value) in patch)
            {
                var field = Fixture.Entity.Fields.Single(f => f.Name == name);
                var clr = ClrTypeOf(field);
                var parameter = Expression.Parameter(typeof(Dictionary<string, object>), "e");
                var efProperty = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(clr);
                var selector = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(typeof(Dictionary<string, object>), clr),
                    Expression.Call(efProperty, parameter, Expression.Constant(name)),
                    parameter);
                setProperty.MakeGenericMethod(clr).Invoke(builder, [selector, value]);
            }
        };
    }

    private static IQueryable<object?[]> ProjectToArray(IQueryable<Dictionary<string, object>> source, IReadOnlyList<string> fields)
    {
        var parameter = Expression.Parameter(typeof(Dictionary<string, object>), "e");
        var efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;
        var items = fields.Select(name =>
        {
            var field = Fixture.Entity.Fields.Single(f => f.Name == name);
            var clr = ClrTypeOf(field);
            var call = Expression.Call(efProperty.MakeGenericMethod(clr), parameter, Expression.Constant(name));
            return (Expression)Expression.Convert(call, typeof(object));
        });

        var body = Expression.NewArrayInit(typeof(object), items);
        var lambda = Expression.Lambda<Func<Dictionary<string, object>, object?[]>>(body, parameter);
        return source.Select(lambda);
    }

    private static Type ClrTypeOf(FieldSchema field) => field.Type switch
    {
        FieldType.Uuid or FieldType.Ref => field.Nullable ? typeof(Guid?) : typeof(Guid),
        FieldType.Integer => field.Nullable ? typeof(long?) : typeof(long),
        FieldType.Decimal => field.Nullable ? typeof(decimal?) : typeof(decimal),
        FieldType.Boolean => field.Nullable ? typeof(bool?) : typeof(bool),
        FieldType.DateTime => field.Nullable ? typeof(DateTimeOffset?) : typeof(DateTimeOffset),
        _ => typeof(string),
    };

    // ---------------------------------------------------------------- Q5

    private async Task Q5_Writes()
    {
        var newId = Guid.NewGuid();

        Section("Q5a  INSERT through a property bag (change tracker)");
        await using var connection = await OpenAsync();
        await using (var context = NewContext(connection))
        {
            try
            {
                context.Rows(Fixture.EntityName).Add(new Dictionary<string, object>
                {
                    ["id"] = newId,
                    ["tenant_id"] = Fixture.AcmeTenant,
                    ["owner_id"] = Fixture.AliceId,
                    ["plate"] = "NEW-001",
                    ["status"] = "open",
                    ["secret_note"] = "new-secret",
                    ["mileage"] = 10L,
                    ["price"] = 1234.56m,
                    ["is_active"] = true,
                    ["created_at"] = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
                });
                await context.SaveChangesAsync();
                DumpCapture();
                Note("insert: OK");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        var policy = Render("owner_id == @user.id", "alvo_p");
        var denyPolicy = Render("owner_id == @user.id", "alvo_p");

        Section("Q5b  ExecuteUpdate over a FromSql root carrying the policy predicate");
        await using (var context = NewContext(connection))
        {
            var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql}";
            try
            {
                var affected = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, ToParameters(policy))
                    .Where(e => EF.Property<Guid>(e, "id") == newId)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<string>(e, "status"), "closed"));
                DumpCapture();
                Note($"rows affected: {affected}");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q5c  ExecuteUpdate where the policy predicate must exclude the row (Bob's row, Alice's policy)");
        await using (var context = NewContext(connection))
        {
            var sql = $"SELECT * FROM {Table()} WHERE {denyPolicy.Sql}";
            try
            {
                var affected = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, ToParameters(denyPolicy))
                    .Where(e => EF.Property<Guid>(e, "id") == Fixture.BobCar)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<string>(e, "status"), "hijacked"));
                DumpCapture();
                Note($"rows affected: {affected} (0 == policy held)");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q5d  tracked UPDATE through a property bag (Attach + set + SaveChanges)");
        await using (var context = NewContext(connection))
        {
            try
            {
                var row = await context.Rows(Fixture.EntityName).FirstAsync(e => EF.Property<Guid>(e, "id") == newId);
                _capture.Clear();
                row["mileage"] = 99L;
                await context.SaveChangesAsync();
                DumpCapture();
                Note("tracked update: OK — note the WHERE clause EF built (id only, no policy predicate)");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q5e  post-image read-back in the SAME transaction after ExecuteUpdate");
        await using (var context = NewContext(connection))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql}";
            try
            {
                await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, ToParameters(policy))
                    .Where(e => EF.Property<Guid>(e, "id") == newId)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<long?>(e, "mileage"), 4242L));

                var post = await context.Rows(Fixture.EntityName)
                    .AsNoTracking()
                    .FirstAsync(e => EF.Property<Guid>(e, "id") == newId);
                DumpCapture();
                Note("post-image: " + Row(post));
                await transaction.RollbackAsync();
                Note("rolled back — the post-image was visible before commit");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
                await transaction.RollbackAsync();
            }
        }

        Section("Q5f  single-statement UPDATE ... RETURNING * with the policy predicate (option b)");
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE {Table()} SET {Fixture.Quote("status")} = @new_status "
                + $"WHERE {Fixture.Quote("id")} = @row_id AND ({policy.Sql}) RETURNING *;";
            command.Parameters.Add(engine.CreateParameter("@new_status", "returned"));
            command.Parameters.Add(Bound("@row_id", newId));
            foreach (var parameter in ToParameters(policy))
            {
                command.Parameters.Add(parameter);
            }

            Console.Write(SqlCapture.Describe(command));
            await using var reader = await command.ExecuteReaderAsync();
            var post = new Dictionary<string, object>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    post[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                }
            }

            Note($"RETURNING gave {post.Count} columns: " + Row(post));
        }
        catch (Exception exception)
        {
            Fail(exception);
        }

        Section("Q5h  ExecuteUpdate with a DYNAMICALLY built setter list (3 fields, 3 CLR types)");
        await using (var context = NewContext(connection))
        {
            var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql}";
            var patch = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "patched",
                ["mileage"] = 777L,
                ["price"] = 9.99m,
                ["is_active"] = false,
                ["owner_id"] = (Guid?)Fixture.AliceId,
            };
            try
            {
                var affected = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, ToParameters(policy))
                    .Where(e => EF.Property<Guid>(e, "id") == newId)
                    .ExecuteUpdateAsync(BuildSetters(patch));
                DumpCapture();
                Note($"rows affected: {affected}");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q5i  row lock on the pre-image read (SELECT ... FOR UPDATE) through a property bag");
        await using (var context = NewContext(connection))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var lockClause = engine is PostgresSpikeEngine ? " FOR UPDATE" : string.Empty;
            var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql}{lockClause}";
            Note($"raw SQL: {sql}");
            try
            {
                var rows = await context.Rows(Fixture.EntityName).FromSqlRaw(sql, ToParameters(policy)).AsNoTracking().ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count}{(lockClause.Length == 0 ? " (SQLite has no FOR UPDATE; a write transaction serializes instead)" : " (locked)")}");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }

            await transaction.RollbackAsync();
        }

        Section("Q5g  ExecuteDelete over a FromSql root carrying the policy predicate");
        await using (var context = NewContext(connection))
        {
            var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql}";
            try
            {
                var affected = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, ToParameters(policy))
                    .Where(e => EF.Property<Guid>(e, "id") == newId)
                    .ExecuteDeleteAsync();
                DumpCapture();
                Note($"rows deleted: {affected}");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }
    }

    // ---------------------------------------------------------------- Q6

    private async Task Q6_Parameters()
    {
        Section("Q6a  default prefix 'p' collides with EF's own FromSqlRaw positional parameters");

        var policy = Render("owner_id == @user.id", "p");
        Note($"predicate with default prefix: {policy.Sql}");

        await using var connection = await OpenAsync();
        await using (var context = NewContext(connection))
        {
            // {0} makes EF mint its OWN @p0 for the caller filter — the same name the predicate uses.
            var sql = $"SELECT * FROM {Table()} WHERE {policy.Sql} AND {Fixture.Quote("status")} <> {{0}}";
            var args = new List<object?> { "never" };
            args.AddRange(policy.Parameters.Select(kv => (object?)Bound("@" + kv.Key, kv.Value)));
            try
            {
                var rows = await context.Rows(Fixture.EntityName).FromSqlRaw(sql, [.. args]).AsNoTracking().ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count} — no error, so check the parameter list above for a duplicated name");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q6b  prefix 'alvo_p' — disjoint from EF's @p0 and @__p_0");
        var safe = Render("owner_id == @user.id", "alvo_p");
        var closureValue = "open";
        await using (var context = NewContext(connection))
        {
            var sql = $"SELECT * FROM {Table()} WHERE {safe.Sql} AND {Fixture.Quote("plate")} <> {{0}}";
            var args = new List<object?> { "never" };
            args.AddRange(safe.Parameters.Select(kv => (object?)Bound("@" + kv.Key, kv.Value)));
            try
            {
                var rows = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw(sql, [.. args])
                    .Where(e => EF.Property<string>(e, "status") == closureValue)
                    .AsNoTracking()
                    .ToListAsync();
                DumpCapture();
                Note($"rows: {rows.Count}");
            }
            catch (Exception exception)
            {
                DumpCapture();
                Fail(exception);
            }
        }

        Section("Q6d  hand-bound vs EF-type-mapped parameter values (the same predicate, both bindings)");
        await using (var context = NewContext(connection))
        {
            var sql = $"SELECT * FROM {Table()} WHERE {safe.Sql}";
            var mapped = ToParameters(safe);
            var handBound = ToParametersHandBound(safe);
            Note($"EF-mapped  : {mapped[0].ParameterName} DbType={mapped[0].DbType} value='{mapped[0].Value}' ({mapped[0].Value?.GetType().Name})");
            Note($"hand-bound : {handBound[0].ParameterName} DbType={handBound[0].DbType} value='{handBound[0].Value}' ({handBound[0].Value?.GetType().Name})");
            _capture.Clear();
            var mappedRows = await context.Rows(Fixture.EntityName).FromSqlRaw(sql, ToParameters(safe)).AsNoTracking().ToListAsync();
            var handRows = await context.Rows(Fixture.EntityName).FromSqlRaw(sql, ToParametersHandBound(safe)).AsNoTracking().ToListAsync();
            _capture.Clear();
            Note($"rows with the EF-mapped binding  : {mappedRows.Count}");
            Note($"rows with the hand-bound binding : {handRows.Count}");
        }

        Section("Q6c  what does the prefix validation accept? (renderer contract)");
        foreach (var prefix in new[] { "p", "alvo_p", "_a", "a1", "@p", "p-1", "1p", "", "p;--" })
        {
            try
            {
                var rendered = Render("owner_id == @user.id", prefix);
                Note($"prefix {prefix,-8} -> accepted, sql = {rendered.Sql}");
            }
            catch (Exception exception)
            {
                Note($"prefix {prefix,-8} -> {exception.GetType().Name}: {exception.Message.Split('\n')[0]}");
            }
        }
    }

    // ---------------------------------------------------------------- Q7

    private async Task Q7_OneTransaction()
    {
        Section("Q7  policy check + data change + an outbox row on ONE DbTransaction");

        var policy = Render("owner_id == @user.id", "alvo_p");
        await using var connection = await OpenAsync();

        // A stand-in for PR5's outbox table, written with hand-rolled ADO.NET on the same transaction.
        var outbox = engine.Schema is null ? Fixture.Quote("alvo_outbox") : $"{Fixture.Quote(engine.Schema)}.{Fixture.Quote("alvo_outbox")}";
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE IF NOT EXISTS {outbox} ({Fixture.Quote("id")} {engine.ColumnType(FieldType.Uuid)} PRIMARY KEY, {Fixture.Quote("payload")} {engine.ColumnType(FieldType.String)});";
            await create.ExecuteNonQueryAsync();
        }

        await using var context = NewContext(connection);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var dbTransaction = transaction.GetDbTransaction();
        Note($"DbTransaction: {dbTransaction.GetType().Name} on connection {dbTransaction.Connection == connection}");

        try
        {
            var visible = await context.Rows(Fixture.EntityName)
                .FromSqlRaw($"SELECT * FROM {Table()} WHERE {policy.Sql}", ToParameters(policy))
                .Where(e => EF.Property<Guid>(e, "id") == Fixture.AliceCar)
                .AsNoTracking()
                .ToListAsync();
            Note($"policy SELECT inside the transaction: {visible.Count} row(s)");

            await context.Rows(Fixture.EntityName)
                .FromSqlRaw($"SELECT * FROM {Table()} WHERE {policy.Sql}", ToParameters(policy))
                .Where(e => EF.Property<Guid>(e, "id") == Fixture.AliceCar)
                .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<string>(e, "status"), "tx-updated"));

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = dbTransaction;
                insert.CommandText = $"INSERT INTO {outbox} ({Fixture.Quote("id")}, {Fixture.Quote("payload")}) VALUES (@id, @payload);";
                insert.Parameters.Add(Bound("@id", Guid.NewGuid()));
                insert.Parameters.Add(engine.CreateParameter("@payload", "{\"type\":\"vehicle.updated\"}"));
                await insert.ExecuteNonQueryAsync();
            }

            DumpCapture();
            await transaction.RollbackAsync();
            Note("rolled back — verifying both the update and the outbox row vanished:");

            await using var verify = NewContext(connection);
            var after = await verify.Rows(Fixture.EntityName).AsNoTracking().FirstAsync(e => EF.Property<Guid>(e, "id") == Fixture.AliceCar);
            _capture.Clear();
            Note($"vehicle.status after rollback = {after["status"]}");
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {outbox};";
            Note($"outbox rows after rollback    = {await count.ExecuteScalarAsync()}");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
            await transaction.RollbackAsync();
        }
    }

    // ---------------------------------------------------------------- Q8

    private async Task Q8_IdentifierQuoting()
    {
        Section("Q8  ISqlGenerationHelper per provider");

        await using var connection = await OpenAsync();
        await using var context = NewContext(connection);
        var helper = context.GetService<ISqlGenerationHelper>();
        Note($"implementation                      : {helper.GetType().FullName}");
        Note($"DelimitIdentifier(\"plate\")           : {helper.DelimitIdentifier("plate")}");
        Note($"DelimitIdentifier(\"we\"ird\")          : {helper.DelimitIdentifier("we\"ird")}");
        Note($"DelimitIdentifier(\"vehicle\",\"alvo\")  : {helper.DelimitIdentifier("vehicle", "alvo")}");
        Note($"DelimitIdentifier(\"vehicle\", null)   : {helper.DelimitIdentifier("vehicle", null)}");
        Note($"StatementTerminator                 : '{helper.StatementTerminator}'");
        Note($"GenerateParameterName(\"alvo_p0\")     : {helper.GenerateParameterName("alvo_p0")}");
        Note($"GenerateParameterNamePlaceholder     : {helper.GenerateParameterNamePlaceholder("alvo_p0")}");

        Section("Q8b  does a schema-qualified FromSqlRaw work on this engine?");
        var qualified = engine.Schema is null
            ? Fixture.Quote(Fixture.EntityName)
            : helper.DelimitIdentifier(Fixture.EntityName, engine.Schema);
        try
        {
            var rows = await context.Rows(Fixture.EntityName)
                .FromSqlRaw($"SELECT * FROM {qualified}")
                .AsNoTracking()
                .ToListAsync();
            DumpCapture();
            Note($"SELECT * FROM {qualified} -> {rows.Count} rows");
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }
    }

    // ---------------------------------------------------------------- extras

    private async Task X1_MalformedQueryExceptionShape()
    {
        Section("X1  exception shapes a malformed property-bag query produces");

        await using var connection = await OpenAsync();

        await using (var context = NewContext(connection))
        {
            try
            {
                _ = await context.Rows(Fixture.EntityName).AsNoTracking()
                    .Where(e => EF.Property<string>(e, "no_such_field") == "x").ToListAsync();
            }
            catch (Exception exception)
            {
                Note("unknown field in a LINQ Where:");
                Fail(exception);
            }

            _capture.Clear();
        }

        await using (var context = NewContext(connection))
        {
            try
            {
                _ = await context.Rows(Fixture.EntityName)
                    .FromSqlRaw($"SELECT * FROM {Table()} WHERE {Fixture.Quote("no_such_column")} = 1")
                    .AsNoTracking().ToListAsync();
            }
            catch (Exception exception)
            {
                Note("unknown column inside FromSqlRaw:");
                Fail(exception);
            }

            _capture.Clear();
        }

        await using (var context = NewContext(connection))
        {
            try
            {
                _ = await context.Rows("no_such_entity").AsNoTracking().ToListAsync();
            }
            catch (Exception exception)
            {
                Note("unknown entity name on Set<>():");
                Fail(exception);
            }

            _capture.Clear();
        }
    }

    private async Task X2_DynamicEntityJsonPath()
    {
        Section("X2  F7 rehearsal: the same property bag over a JSON-projecting FromSql (dynamic entity)");

        var jsonType = engine is PostgresSpikeEngine ? "jsonb" : "TEXT";
        var records = engine.Schema is null
            ? Fixture.Quote("alvo_records")
            : $"{Fixture.Quote(engine.Schema)}.{Fixture.Quote("alvo_records")}";

        await using var connection = await OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                $"DROP TABLE IF EXISTS {records}; CREATE TABLE {records} ("
                + $"{Fixture.Quote("id")} {engine.ColumnType(FieldType.Uuid)} PRIMARY KEY, "
                + $"{Fixture.Quote("entity")} {engine.ColumnType(FieldType.String)} NOT NULL, "
                + $"{Fixture.Quote("data")} {jsonType} NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        var recordId = Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO {records} ({Fixture.Quote("id")},{Fixture.Quote("entity")},{Fixture.Quote("data")}) VALUES (@id,@entity,{(engine is PostgresSpikeEngine ? "CAST(@data AS jsonb)" : "@data")});";
            insert.Parameters.Add(Bound("@id", recordId));
            insert.Parameters.Add(engine.CreateParameter("@entity", "evidencia"));
            insert.Parameters.Add(engine.CreateParameter(
                "@data",
                $"{{\"tenant_id\":\"{Fixture.AcmeTenant}\",\"owner_id\":\"{Fixture.AliceId}\",\"plate\":\"DYN-001\",\"status\":\"open\",\"secret_note\":\"dyn\",\"mileage\":7,\"price\":\"1.00\",\"is_active\":true,\"created_at\":\"2026-01-01 00:00:00+00:00\"}}"));
            await insert.ExecuteNonQueryAsync();
        }

        // A dynamic-entity IFieldSqlRenderer would render a field as a JSON path, not a column.
        string JsonField(string name) => engine is PostgresSpikeEngine
            ? $"{Fixture.Quote("data")} ->> '{name}'"
            : $"json_extract({Fixture.Quote("data")}, '$.{name}')";

        var projection = new StringBuilder($"SELECT {Fixture.Quote("id")} AS {Fixture.Quote("id")}");
        foreach (var field in Fixture.Entity.Fields.Where(f => f.Name != "id"))
        {
            var cast = engine is PostgresSpikeEngine
                ? field.Type switch
                {
                    FieldType.Uuid => $"CAST({JsonField(field.Name)} AS uuid)",
                    FieldType.Integer => $"CAST({JsonField(field.Name)} AS bigint)",
                    FieldType.Decimal => $"CAST({JsonField(field.Name)} AS numeric(18,2))",
                    FieldType.Boolean => $"CAST({JsonField(field.Name)} AS boolean)",
                    FieldType.DateTime => $"CAST({JsonField(field.Name)} AS timestamptz)",
                    _ => JsonField(field.Name),
                }
                : JsonField(field.Name);
            projection.Append($", {cast} AS {Fixture.Quote(field.Name)}");
        }

        projection.Append($" FROM {records} WHERE {Fixture.Quote("entity")} = @entity_name");

        var policySql = engine is PostgresSpikeEngine
            ? $"COALESCE(CAST({Fixture.Quote("data")} ->> 'owner_id' AS uuid) = @alvo_p0, FALSE)"
            : $"COALESCE(json_extract({Fixture.Quote("data")}, '$.owner_id') = @alvo_p0, 0)";
        var sql = projection.ToString().Replace("WHERE " + Fixture.Quote("entity") + " = @entity_name", $"WHERE {Fixture.Quote("entity")} = @entity_name AND {policySql}", StringComparison.Ordinal);

        await using var context = NewContext(connection);
        try
        {
            var rows = await context.Rows(Fixture.EntityName)
                .FromSqlRaw(sql, engine.CreateParameter("@entity_name", "evidencia"), Bound("@alvo_p0", Fixture.AliceId))
                .AsNoTracking()
                .ToListAsync();
            DumpCapture();
            Note($"rows: {rows.Count} (EF-mapped Guid parameter)");
            if (rows.Count > 0)
            {
                Note("  " + Row(rows[0]));
            }
        }
        catch (Exception exception)
        {
            DumpCapture();
            Fail(exception);
        }

        Section("X2b  the same F7 query with the Guid bound as raw lower-case text");
        await using (var context2 = NewContext(connection))
        {
            try
            {
                var rows = await context2.Rows(Fixture.EntityName)
                    .FromSqlRaw(
                        sql,
                        engine.CreateParameter("@entity_name", "evidencia"),
                        engine.CreateParameter("@alvo_p0", Fixture.AliceId.ToString()))
                    .AsNoTracking()
                    .ToListAsync();
                _capture.Clear();
                Note($"rows: {rows.Count} (lower-case text parameter)");
            }
            catch (Exception exception)
            {
                _capture.Clear();
                Note("lower-case text parameter:");
                Fail(exception);
            }
        }

        Section("X2c  how does EF's own Guid mapping render a Guid literal/parameter on this engine?");
        var guidMapping = _mappings.FindMapping(typeof(Guid))!;
        Note($"mapping           : {guidMapping.GetType().Name}, store type {guidMapping.StoreType}");
        Note($"literal for Alice : {guidMapping.GenerateSqlLiteral(Fixture.AliceId)}");
        using (var command = _binderConnection.CreateCommand())
        {
            var parameter = guidMapping.CreateParameter(command, "@g", Fixture.AliceId);
            Note($"parameter value   : '{parameter.Value}' ({parameter.Value?.GetType().Name}), DbType={parameter.DbType}");
        }
    }
}
