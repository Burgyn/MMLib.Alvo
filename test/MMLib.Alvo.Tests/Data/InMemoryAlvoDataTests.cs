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

        var likeExactCase = (await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.Like, "Hello%")), caller, ct)).Items;
        var likeWrongCase = (await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.Like, "hello%")), caller, ct)).Items;
        var ilikeWrongCase = (await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.ILike, "hello%")), caller, ct)).Items;

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
        var result = (await data.QueryAsync(Query("items", new AlvoComparison("title", AlvoFilterOperator.Like, pathologicalPattern)), caller, ct)).Items;
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

        var result = (await data.QueryAsync(Query("items", new AlvoComparison("amount", AlvoFilterOperator.Neq, 5m)), caller, ct)).Items;

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

        var result = (await data.QueryAsync(
            Query("items", new AlvoNot(new AlvoComparison("amount", AlvoFilterOperator.Eq, 5m))), caller, ct)).Items;

        result.Count.ShouldBe(1);
        result[0]["amount"].ShouldBe(10m);
    }

    /// <summary>
    /// A <see langword="string"/> operand to <c>in</c> is <b>refused</b>, never iterated as a sequence of
    /// characters — <see langword="string"/> itself satisfies <see cref="System.Collections.IEnumerable"/>,
    /// which would otherwise give a caller who forgot to wrap a single value in a list silent per-character
    /// membership testing.
    /// </summary>
    /// <remarks>
    /// It used to be excluded rather than refused, which is <em>almost</em> as bad: the shipped backends refuse
    /// it, so one request was an empty page here and a refusal there. A malformed query is the port's
    /// <see cref="ArgumentException"/> channel, and both implementations now word it identically.
    /// </remarks>
    [Fact]
    public async Task In_with_a_string_operand_is_refused_rather_than_iterated_as_characters()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", StringField(),
            Row(Guid.NewGuid(), ("title", "o")));
        var caller = Caller();

        await Should.ThrowAsync<ArgumentException>(() => data.QueryAsync(
            Query("items", new AlvoComparison("title", AlvoFilterOperator.In, "ok")), caller, ct));
    }

    /// <summary>
    /// A numeric comparison against a value outside <see langword="decimal"/>'s range is refused on the
    /// port's malformed-query channel, not with a raw <see cref="OverflowException"/> and not with a silent
    /// empty page: a shipped backend cannot bind it through a <c>decimal</c> column either, and the two
    /// implementations of this port must answer one way.
    /// </summary>
    [Fact]
    public async Task A_numeric_comparison_outside_decimal_range_is_refused_rather_than_overflowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", DecimalField("amount"),
            Row(Guid.NewGuid(), ("amount", 5m)));
        var caller = Caller();

        await Should.ThrowAsync<ArgumentException>(() => data.QueryAsync(
            Query("items", new AlvoComparison("amount", AlvoFilterOperator.Eq, double.MaxValue)), caller, ct));
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

        var nullsFirst = (await data.QueryAsync(
            new AlvoQuery { Entity = "items", Sort = [new AlvoSort("rank", Descending: false, Nulls: AlvoNullPlacement.First)] }, caller, ct)).Items;
        var nullsLastDescending = (await data.QueryAsync(
            new AlvoQuery { Entity = "items", Sort = [new AlvoSort("rank", Descending: true, Nulls: AlvoNullPlacement.Last)] }, caller, ct)).Items;

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

        var limited = (await data.QueryAsync(new AlvoQuery { Entity = "items", Limit = 1 }, caller, ct)).Items;
        var unrecognizedCursor = (await data.QueryAsync(new AlvoQuery { Entity = "items", After = Guid.NewGuid().ToString() }, caller, ct)).Items;

        limited.Count.ShouldBe(1);
        unrecognizedCursor.ShouldBeEmpty();
    }

    /// <summary>
    /// The round-trip case the cursor tests above never exercised: paging with a cursor the store
    /// actually issued (the previous page's last row id) must resume immediately after that row,
    /// not restart from the beginning and not skip a row — the only way a caller ever legitimately
    /// obtains an <see cref="AlvoQuery.After"/> value.
    /// </summary>
    [Fact]
    public async Task Paging_with_a_cursor_the_store_actually_issued_resumes_after_that_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", StringField(),
            Row(Guid.NewGuid(), ("title", "a")),
            Row(Guid.NewGuid(), ("title", "b")),
            Row(Guid.NewGuid(), ("title", "c")));
        var caller = Caller();

        var everything = (await data.QueryAsync(new AlvoQuery { Entity = "items" }, caller, ct)).Items;
        var firstPage = (await data.QueryAsync(new AlvoQuery { Entity = "items", Limit = 1 }, caller, ct)).Items;
        var issuedCursor = firstPage[0]["id"]!.ToString();
        var secondPage = (await data.QueryAsync(new AlvoQuery { Entity = "items", Limit = 1, After = issuedCursor }, caller, ct)).Items;

        firstPage.Select(row => row["id"]).ShouldBe([everything[0]["id"]]);
        secondPage.Select(row => row["id"]).ShouldBe([everything[1]["id"]]);
    }

    /// <summary>
    /// An operator no <see cref="AlvoFilterOperator"/> member names — reachable only by casting an
    /// integer — is a malformed query, so it is <b>refused</b> rather than folded into <c>UNKNOWN</c>. The
    /// negation is what makes the distinction matter: a "unrecognized operator = false" default would match
    /// every row once negated, and a refusal cannot be inverted at all.
    /// </summary>
    [Fact]
    public async Task Not_of_an_unrecognized_operator_is_refused_rather_than_inverted()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore(
            "items", StringField(),
            Row(Guid.NewGuid(), ("title", "a")),
            Row(Guid.NewGuid(), ("title", "b")));
        var caller = Caller();

        await Should.ThrowAsync<ArgumentException>(() => data.QueryAsync(
            Query("items", new AlvoNot(new AlvoComparison("title", (AlvoFilterOperator)99, "a"))), caller, ct));
    }

    /// <summary>
    /// A payload key the entity's schema does not declare is rejected on the port's own documented
    /// failure contract — <see cref="AlvoAuthorizationException"/>, the same class of refusal every
    /// other unwritable-field rejection uses — and names neither the entity nor the offending key: the
    /// key is caller-supplied text, and a message naming both would be a schema-shape oracle answering
    /// "does this entity have a field called X?" one request at a time.
    /// </summary>
    [Fact]
    public async Task A_payload_key_the_schema_does_not_declare_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStore("items", StringField());
        var caller = Caller();

        var ex = await Should.ThrowAsync<AlvoAuthorizationException>(() => data.CreateAsync(
            "items", new Dictionary<string, object?> { ["title"] = "x", ["bogus"] = "y" }, caller, ct));

        ex.Message.ShouldNotContain("bogus");
        ex.Message.ShouldNotContain("items");
    }

    /// <summary>
    /// An entity this store's schema does not know must <b>deny</b> a write, never skip the payload
    /// check and write it anyway. Reaching this needs a policy catalog that knows an entity the store's
    /// own schema does not — the exact inconsistency the guard exists for — so the catalog here is
    /// built over both entities and the store is handed the narrower schema.
    /// </summary>
    [Fact]
    public async Task A_write_to_an_entity_absent_from_the_stores_schema_denies_rather_than_skipping_validation()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = CreateStoreWithUnknownEntity("items", "ghosts", StringField());
        var caller = Caller();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.CreateAsync(
            "ghosts", new Dictionary<string, object?> { ["title"] = "x" }, caller, ct));
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
        var schema = new SchemaModel([EntitySchemaOf(entity, fields)]);
        var data = new InMemoryAlvoData(EngineOver(DescriptorOf(fields, entity), schema), new PredicateEvaluator(), schema);
        data.Seed(entity, seed);
        return data;
    }

    /// <summary>
    /// A store whose policy catalog knows one more entity than the store's own schema does — the
    /// descriptor/schema inconsistency <c>InMemoryAlvoData</c>'s entity-absent guard exists for, and the
    /// only way to reach that guard, since a catalog built from the store's own schema would deny the
    /// operation before any payload check runs.
    /// </summary>
    private static InMemoryAlvoData CreateStoreWithUnknownEntity(
        string knownEntity, string absentEntity, Dictionary<string, FieldDescriptor> fields)
    {
        var storeSchema = new SchemaModel([EntitySchemaOf(knownEntity, fields)]);
        var catalogSchema = new SchemaModel([EntitySchemaOf(knownEntity, fields), EntitySchemaOf(absentEntity, fields)]);
        var descriptor = DescriptorOf(fields, knownEntity, absentEntity);
        return new InMemoryAlvoData(EngineOver(descriptor, catalogSchema), new PredicateEvaluator(), storeSchema);
    }

    private static AlvoDescriptor DescriptorOf(Dictionary<string, FieldDescriptor> fields, params string[] entities)
    {
        var entityDescriptor = new EntityDescriptor
        {
            Fields = fields,
            Tenancy = EntityTenancy.Global,
            Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
        };

        return new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "direct-tests",
            Entities = entities.ToDictionary(entity => entity, _ => entityDescriptor, StringComparer.Ordinal),
        };
    }

    private static EntitySchema EntitySchemaOf(string entity, Dictionary<string, FieldDescriptor> fields)
    {
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

        return new EntitySchema { Name = entity, Tenancy = TenancyMode.Global, Fields = schemaFields };
    }

    private static PolicyEngine EngineOver(AlvoDescriptor descriptor, SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, MMLib.Alvo.Tests.Expressions.CelFixtures.Compiler);
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(descriptor.Name, catalog);
        return new PolicyEngine(provider);
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
