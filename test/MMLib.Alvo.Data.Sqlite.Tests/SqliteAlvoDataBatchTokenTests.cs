using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// What an idempotency token does — and does not do — to a batch: it is the only thing that arms the
/// contended-write retry, it bounds that retry at three attempts rather than a single row's ten, and it is
/// <b>not</b> spent by a batch that refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured as a cost, not only as an outcome.</b> A build that retries when it should not, and one that
/// gives up an attempt early, both end with the same exception type for the same caller — so the three facts
/// that matter here count the entity's own insert attempts. Each count is preceded by a successful write
/// measured through the same matcher, because a matcher that matches nothing passes at zero and measures
/// nothing.
/// </para>
/// <para>
/// <b>The failure is a <c>NOT NULL</c> violation</b>, deliberately: this driver's dialect decodes SQLite's
/// <em>unique</em> and <em>foreign key</em> codes into <see cref="AlvoConstraintViolationException"/>, which
/// leaves on the first attempt by design, so a duplicate could not exercise the retry at all. A missing
/// required value is refused by the database and surfaces as EF's <see cref="DbUpdateException"/> — a storage
/// write failure the loop does re-attempt, which is the shape a rival on the key has.
/// </para>
/// </remarks>
public sealed class SqliteAlvoDataBatchTokenTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>
    /// A batch carrying a token re-attempts a failing write three times and then surfaces this port's own
    /// invariant failure, naming how many attempts it made.
    /// </summary>
    /// <remarks>
    /// Three rather than a single row's ten because each wasted attempt costs N inserts; the number is in the
    /// message because an operator reading a 500 has nothing else to tell sustained contention from a write
    /// that violates a constraint on its own.
    /// </remarks>
    [Fact]
    public async Task A_contended_batch_with_a_token_stops_after_three_attempts()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        await ProveTheMatcherCountsAsync(host);

        host.ClearStatements();
        var exhausted = await Should.ThrowAsync<InvalidOperationException>(() => host.Data.CreateManyAsync(
            Entity, [WithoutTheRequiredValue()], Caller, Token(), Ct));

        exhausted.Message.ShouldContain($"{ContendedBatchAttempts} times");
        InsertsIntoTheEntity(host).ShouldBe(
            ContendedBatchAttempts, $"statements were: {string.Join(" | ", host.Statements)}");
    }

    /// <summary>
    /// A batch carrying <b>no</b> token is not retried at all, and the engine's own failure reaches the caller
    /// untranslated.
    /// </summary>
    /// <remarks>
    /// The retry exists for one thing — a rival committing the same key first, which fails the record insert
    /// and nothing else. A batch with no token performs no record insert, so re-running N inserts, N hooks and
    /// N outbox rows would be waste for a failure no retry can fix, and it would also relabel the engine's own
    /// refusal as this port's invariant violation.
    /// </remarks>
    [Fact]
    public async Task A_batch_with_no_token_is_not_retried_at_all()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        await ProveTheMatcherCountsAsync(host);

        host.ClearStatements();
        await Should.ThrowAsync<DbUpdateException>(() => host.Data.CreateManyAsync(
            Entity, [WithoutTheRequiredValue()], Caller, cancellationToken: Ct));

        InsertsIntoTheEntity(host).ShouldBe(1, $"statements were: {string.Join(" | ", host.Statements)}");
    }

    /// <summary>
    /// A batch that <b>refused</b> spends no key: the same token, sent again, is judged again rather than
    /// answered as a replay of a write that never happened.
    /// </summary>
    /// <remarks>
    /// A record filed for a refused batch names no rows at all, so every later replay of that key would report
    /// a successful write of zero rows — a refusal turning into a success on the caller's own retry, which is
    /// the failure mode idempotency exists to remove.
    /// </remarks>
    [Fact]
    public async Task A_refused_batch_spends_no_idempotency_key()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var token = Token();
        var absent = Guid.NewGuid();

        var first = await host.Data.DeleteManyAsync(Entity, [absent], Caller, token, Ct);
        var second = await host.Data.DeleteManyAsync(Entity, [absent], Caller, token, Ct);

        first.Succeeded.ShouldBeFalse("an absent row refuses the batch");
        second.Succeeded.ShouldBeFalse("and the key it never spent refuses it again");
    }

    /// <summary>
    /// A batch that violates nothing lands, and inserts exactly once through the matcher the counts above
    /// use — so a count assertion that matched nothing could not pass for the wrong reason.
    /// </summary>
    private static async Task ProveTheMatcherCountsAsync(AlvoDataHost host)
    {
        host.ClearStatements();
        await host.Data.CreateManyAsync(Entity, [Payload("ok")], Caller, Token(), Ct);
        InsertsIntoTheEntity(host).ShouldBe(
            1, "a batch that violates nothing inserts once — otherwise the counts below measure nothing");
    }

    /// <summary>
    /// Counts the entity's own inserts, so the number means write attempts rather than statements. The
    /// idempotency table's own <c>INSERT</c> is a hand-built command on the raw connection and never reaches
    /// EF's interception at all, but naming the table keeps the count right if that ever changes.
    /// </summary>
    private static int InsertsIntoTheEntity(AlvoDataHost host) =>
        host.Statements.Count(statement =>
            statement.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
            && statement.Contains($"\"{Entity}\"", StringComparison.Ordinal));

    /// <summary>What <c>EfAlvoData.ContendedBatchAttempts</c> is.</summary>
    /// <remarks>
    /// Restated rather than read from the port — it is private there, and a fact importing the number it
    /// asserts against could not fail if that number changed.
    /// </remarks>
    private const int ContendedBatchAttempts = 3;

    private const string Entity = "notes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh key each time, so nothing here is a replay — the failure must come from the row.</summary>
    private static AlvoIdempotency Token() => new($"key-{Guid.NewGuid():N}", $"{Entity}:digest");

    private static Dictionary<string, object?> Payload(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title };

    /// <summary>
    /// A payload the descriptor admits and the database refuses: <c>title</c> is <c>NOT NULL</c>, and
    /// schema-derived request validation belongs above this port.
    /// </summary>
    private static Dictionary<string, object?> WithoutTheRequiredValue() => new(StringComparer.Ordinal);

    private static AlvoContext Caller { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "batch-token-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String, Required = true },
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
                new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 100 },
            ],
        },
    ]);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
