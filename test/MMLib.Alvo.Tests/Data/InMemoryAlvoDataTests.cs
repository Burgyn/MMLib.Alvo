using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Diagnostics;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// Direct, non-adversarial <see cref="InMemoryAlvoData"/> tests: general query mechanics (filter
/// operator null semantics, sort, paging) and payload validation that
/// <see cref="AlvoDataAdversarialTests"/> does not cover, since that suite is scoped to
/// security-relevant facts, not general CRUD/query correctness.
/// </summary>
public class InMemoryAlvoDataTests
{
    /// <summary>A pattern match is <c>UNKNOWN</c> (never a match) against a <see langword="null"/> field or a non-string pattern, and <c>like</c>/<c>ilike</c> differ only in case sensitivity.</summary>
    [Fact]
    public async Task Like_is_case_sensitive_and_ilike_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        var rowId = Guid.NewGuid();
        var data = CreateStore(
            "items", StringField(),
            Row(rowId, ("title", "Hello World")));
        var caller = Caller();

        var likeExactCase = await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.Like, "Hello%")), caller, ct);
        var likeWrongCase = await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.Like, "hello%")), caller, ct);
        var ilikeWrongCase = await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.ILike, "hello%")), caller, ct);

        likeExactCase.Count.ShouldBe(1);
        likeWrongCase.ShouldBeEmpty();
        ilikeWrongCase.Count.ShouldBe(1);
    }

    /// <summary>
    /// Regression guard: the pattern is caller-controlled (PR3 takes it straight off a query
    /// string), so a long run of <c>%</c> wildcards must not cause catastrophic regex
    /// backtracking. <see cref="AlvoFilterEvaluator"/> runs the translated pattern under
    /// <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/> specifically to
    /// rule this out.
    /// </summary>
    [Fact]
    public async Task A_long_wildcard_run_does_not_cause_catastrophic_backtracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", StringField(),
            Row(Guid.NewGuid(), ("title", new string('a', 40))));
        var caller = Caller();
        var pathologicalPattern = string.Concat(Enumerable.Repeat("%", 30)) + "x";

        var stopwatch = Stopwatch.StartNew();
        var result = await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.Like, pathologicalPattern)), caller, ct);
        stopwatch.Stop();

        result.ShouldBeEmpty();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    /// <summary><c>neq</c> against a <see langword="null"/> field is <c>UNKNOWN</c>, matching SQL's <c>&lt;&gt;</c> — never a match, unlike a naive two-valued negation.</summary>
    [Fact]
    public async Task Neq_does_not_match_a_null_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", DecimalField("amount"),
            Row(Guid.NewGuid(), ("amount", 5m)),
            Row(Guid.NewGuid(), ("amount", 7m)),
            Row(Guid.NewGuid(), ("amount", null)));
        var caller = Caller();

        var result = await data.QueryAsync(Query("items", new AlvoComparison("amount", AlvoFilterOperator.Neq, 5m)), caller, ct);

        result.Count.ShouldBe(1);
        result[0]["amount"].ShouldBe(7m);
    }

    /// <summary>
    /// <c>NOT</c> of an unresolved (<c>UNKNOWN</c>) comparison stays unresolved rather than
    /// flipping into a match — the SQL-faithful behavior <see cref="AlvoNot"/> must have. A naive
    /// two-valued <c>!Matches(...)</c> would incorrectly include the null-valued row here.
    /// </summary>
    [Fact]
    public async Task Not_of_an_unresolved_comparison_stays_unmatched_rather_than_flipping_to_a_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", DecimalField("amount"),
            Row(Guid.NewGuid(), ("amount", 5m)),
            Row(Guid.NewGuid(), ("amount", 10m)),
            Row(Guid.NewGuid(), ("amount", null)));
        var caller = Caller();

        var result = await data.QueryAsync(
            Query("items", new AlvoNot(new AlvoComparison("amount", AlvoFilterOperator.Eq, 5m))), caller, ct);

        result.Count.ShouldBe(1);
        result[0]["amount"].ShouldBe(10m);
    }

    /// <summary>
    /// A <see langword="string"/> operand to <c>in</c> is excluded outright (never matched) rather
    /// than iterated as a sequence of characters — <see langword="string"/> itself satisfies
    /// <see cref="System.Collections.IEnumerable"/>, which would otherwise let a caller who forgot
    /// to wrap a single value in a list silently get per-character membership testing instead of a
    /// clear "doesn't match".
    /// </summary>
    [Fact]
    public async Task In_with_a_string_operand_is_excluded_rather_than_iterated_as_characters()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", StringField(),
            Row(Guid.NewGuid(), ("title", "o")));
        var caller = Caller();

        var result = await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.In, "ok")), caller, ct);

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// A numeric comparison against a value outside <see langword="decimal"/>'s range must not
    /// throw <see cref="OverflowException"/> — it simply does not match, since a caller-supplied
    /// filter value must never crash the query.
    /// </summary>
    [Fact]
    public async Task A_numeric_comparison_outside_decimal_range_does_not_throw()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", DecimalField("amount"),
            Row(Guid.NewGuid(), ("amount", 5m)));
        var caller = Caller();

        var result = await data.QueryAsync(
            Query("items", new AlvoComparison("amount", AlvoFilterOperator.Eq, double.MaxValue)), caller, ct);

        result.ShouldBeEmpty();
    }

    /// <summary>Explicit null placement is honored independently of sort direction, and descending order reverses the non-null values.</summary>
    [Fact]
    public async Task Sort_honors_explicit_null_placement_and_descending_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var nullRankId = Guid.NewGuid();
        var oneId = Guid.NewGuid();
        var twoId = Guid.NewGuid();
        var data = CreateStore(
            "items", IntegerField("rank"),
            Row(nullRankId, ("rank", null)),
            Row(oneId, ("rank", 1)),
            Row(twoId, ("rank", 2)));
        var caller = Caller();

        var nullsFirst = await data.QueryAsync(
            new AlvoQuery { Entity = "items", Sort = [new AlvoSort("rank", Descending: false, Nulls: AlvoNullPlacement.First)] }, caller, ct);
        var nullsLastDescending = await data.QueryAsync(
            new AlvoQuery { Entity = "items", Sort = [new AlvoSort("rank", Descending: true, Nulls: AlvoNullPlacement.Last)] }, caller, ct);

        nullsFirst.Select(row => row["id"]).ShouldBe([nullRankId, oneId, twoId]);
        nullsLastDescending.Select(row => row["id"]).ShouldBe([twoId, oneId, nullRankId]);
    }

    /// <summary><c>Limit</c> truncates the result, and a cursor this store never issued reads as an empty final page rather than throwing or restarting from the beginning.</summary>
    [Fact]
    public async Task Limit_and_an_unrecognized_cursor_are_handled_without_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", StringField(),
            Row(Guid.NewGuid(), ("title", "a")),
            Row(Guid.NewGuid(), ("title", "b")));
        var caller = Caller();

        var limited = await data.QueryAsync(new AlvoQuery { Entity = "items", Limit = 1 }, caller, ct);
        var unrecognizedCursor = await data.QueryAsync(new AlvoQuery { Entity = "items", After = Guid.NewGuid().ToString() }, caller, ct);

        limited.Count.ShouldBe(1);
        unrecognizedCursor.ShouldBeEmpty();
    }

    /// <summary>
    /// A payload key the entity's schema does not declare is rejected with an
    /// <see cref="ArgumentException"/> — the in-memory equivalent of the unknown-column SQL error a
    /// real provider would raise, so the fake and a real provider agree on what "not a field at all"
    /// means.
    /// </summary>
    [Fact]
    public async Task A_payload_key_the_schema_does_not_declare_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore("items", StringField());
        var caller = Caller();

        await Should.ThrowAsync<ArgumentException>(() => data.CreateAsync(
            "items", new Dictionary<string, object?> { ["title"] = "x", ["bogus"] = "y" }, caller, ct));
    }

    private static Dictionary<string, FieldDescriptor> StringField() => new(StringComparer.Ordinal)
    {
        ["title"] = new() { Type = DescField.String },
    };

    private static Dictionary<string, FieldDescriptor> DecimalField(string name) => new(StringComparer.Ordinal)
    {
        [name] = new() { Type = DescField.Decimal, Precision = 18, Scale = 2 },
    };

    private static Dictionary<string, FieldDescriptor> IntegerField(string name) => new(StringComparer.Ordinal)
    {
        [name] = new() { Type = DescField.Integer },
    };

    private static AlvoQuery Query(string entity, AlvoFilter filter) => new() { Entity = entity, Filter = filter };

    private static AlvoRecord Row(Guid id, params (string Field, object? Value)[] fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id };
        foreach (var (field, value) in fields)
        {
            values[field] = value;
        }

        return new AlvoRecord(values);
    }

    private static AlvoContext Caller() => new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static InMemoryAlvoData CreateStore(string entity, Dictionary<string, FieldDescriptor> fields, params AlvoRecord[] seed)
    {
        var entityDescriptor = new EntityDescriptor
        {
            Fields = fields,
            Tenancy = EntityTenancy.Global,
            Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
        };
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "direct-tests",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal) { [entity] = entityDescriptor },
        };

        var schemaFields = new List<FieldSchema> { new() { Name = "id", Type = SchemaField.Uuid, Required = true } };
        foreach (var (name, field) in fields)
        {
            schemaFields.Add(new FieldSchema
            {
                Name = name,
                Type = ToSchemaFieldType(field.Type),
                Nullable = field.Nullable ?? field.Required != true,
                Precision = field.Precision,
                Scale = field.Scale,
            });
        }

        var schema = new SchemaModel([new EntitySchema { Name = entity, Tenancy = TenancyMode.Global, Fields = schemaFields }]);

        var catalog = PolicyCatalog.Build(descriptor, schema, MMLib.Alvo.Tests.Expressions.CelFixtures.Compiler);
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(descriptor.Name, catalog);
        var engine = new PolicyEngine(provider);
        var evaluator = new PredicateEvaluator();

        var data = new InMemoryAlvoData(engine, evaluator, schema);
        data.Seed(entity, seed);
        return data;
    }

    private static SchemaField ToSchemaFieldType(DescField type) => type switch
    {
        DescField.String => SchemaField.String,
        DescField.Integer => SchemaField.Integer,
        DescField.Decimal => SchemaField.Decimal,
        DescField.Boolean => SchemaField.Boolean,
        DescField.Uuid => SchemaField.Uuid,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped field type for this test's local fixture."),
    };
}
