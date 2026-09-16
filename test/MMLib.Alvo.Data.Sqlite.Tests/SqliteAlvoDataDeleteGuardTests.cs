using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The guards every delete and every batch runs <b>before</b> a policy is even resolved, over a real SQLite
/// database: a blank entity name, a missing caller, a token no key space can scope, an empty batch, and an
/// entity whose schema this port cannot serve at all.
/// </summary>
/// <remarks>
/// <para>
/// Each of these is a whole family of the failure contract — <see cref="ArgumentException"/> is a malformed
/// request (422) and <see cref="InvalidOperationException"/> is a schema this port refuses (500) — so a guard
/// that stopped running does not crash: it falls through to a policy resolution that answers a <em>denial</em>
/// instead, and the request layer above renders the wrong status for the wrong reason.
/// </para>
/// <para>
/// The delete and the three batch verbs are asserted separately because they reach the guards through
/// different members — <c>DeleteAsync</c> runs its own three, and <c>CreateMany</c>/<c>UpdateMany</c>/
/// <c>DeleteMany</c> share <c>BatchDecision</c>. A theory over the three verbs is what keeps a fourth from
/// arriving without them.
/// </para>
/// </remarks>
public sealed class SqliteAlvoDataDeleteGuardTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>A delete naming no entity is a malformed request, not a denial.</summary>
    [Fact]
    public async Task A_delete_naming_a_blank_entity_is_a_malformed_request()
    {
        var host = await StartAsync();

        await Should.ThrowAsync<ArgumentException>(
            () => host.Data.DeleteAsync("   ", Guid.NewGuid(), Caller(), cancellationToken: Ct));
    }

    /// <summary>
    /// A delete with no <see cref="AlvoContext"/> at all throws rather than defaulting to anyone — a caller
    /// identity is required before a policy can be resolved, and a delete is the write where defaulting is
    /// irrecoverable.
    /// </summary>
    [Fact]
    public async Task A_delete_with_no_context_at_all_throws_rather_than_defaulting_to_anyone()
    {
        var host = await StartAsync();

        await Should.ThrowAsync<ArgumentNullException>(
            () => host.Data.DeleteAsync(Entity, Guid.NewGuid(), null!, cancellationToken: Ct));
    }

    /// <summary>
    /// An idempotency key needs an identity to scope it to, and every anonymous caller carries the same
    /// reserved all-zero one — so a token on a delete is refused exactly as it is on a create.
    /// </summary>
    [Fact]
    public async Task A_delete_token_from_an_anonymous_caller_is_refused()
    {
        var host = await StartAsync();

        await Should.ThrowAsync<ArgumentException>(() => host.Data.DeleteAsync(
            Entity, Guid.NewGuid(), AlvoContext.Anonymous, idempotency: Token(), cancellationToken: Ct));
    }

    /// <summary>A batch naming no entity is a malformed request, on every verb.</summary>
    /// <param name="verb">The batch verb under test.</param>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_batch_naming_a_blank_entity_is_a_malformed_request(BatchVerb verb)
    {
        var host = await StartAsync();

        await Should.ThrowAsync<ArgumentException>(() => Invoke(host, verb, "   ", Caller(), token: null));
    }

    /// <summary>A batch with no caller throws rather than defaulting to anyone, on every verb.</summary>
    /// <param name="verb">The batch verb under test.</param>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_batch_with_no_context_at_all_throws_rather_than_defaulting_to_anyone(BatchVerb verb)
    {
        var host = await StartAsync();

        await Should.ThrowAsync<ArgumentNullException>(() => Invoke(host, verb, Entity, null!, token: null));
    }

    /// <summary>A batch token from an anonymous caller is refused, on every verb.</summary>
    /// <param name="verb">The batch verb under test.</param>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_batch_token_from_an_anonymous_caller_is_refused(BatchVerb verb)
    {
        var host = await StartAsync();

        await Should.ThrowAsync<ArgumentException>(
            () => Invoke(host, verb, Entity, AlvoContext.Anonymous, Token()));
    }

    /// <summary>
    /// An empty batch is refused rather than answered as a write of nothing, and the refusal names the row
    /// count — an intermediary that stripped the body would otherwise look exactly like a success.
    /// </summary>
    /// <remarks>
    /// The parameter name is asserted rather than the sentence: it is the part a request layer maps a field
    /// error from, and the only part of the message this port owes anybody.
    /// </remarks>
    /// <param name="verb">The batch verb under test.</param>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task An_empty_batch_is_refused_naming_the_row_count(BatchVerb verb)
    {
        var host = await StartAsync();

        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => InvokeEmpty(host, verb, Entity, Caller()));

        refusal.ParamName.ShouldBe("rowCount");
    }

    /// <summary>
    /// A <b>batch</b> delete refuses a <c>softDelete</c> entity exactly as a single delete does, and leaves
    /// every named row in place.
    /// </summary>
    /// <remarks>
    /// The single-row refusal is inherited by every implementation; this one is the batch face of the same
    /// fail-closed belt, and it is the one whose absence is silent: a batch that skipped the check would
    /// hard-delete N rows whose schema declares the delete recoverable, which is the worst failure mode this
    /// data path has.
    /// </remarks>
    [Fact]
    public async Task A_batch_delete_on_a_soft_delete_entity_is_refused_rather_than_hard_deleting()
    {
        var host = await _fixture.StartAsync(SoftDeleteSchema, SoftDeleteDescriptor);
        var caller = Caller();
        var kept = (Guid)(await host.Data.CreateAsync(
            Archives, Payload("kept"), caller, cancellationToken: Ct))["id"]!;

        await Should.ThrowAsync<InvalidOperationException>(
            () => host.Data.DeleteManyAsync(Archives, [kept], caller, cancellationToken: Ct));

        (await host.Data.GetAsync(Archives, kept, caller, Ct)).ShouldNotBeNull();
    }

    /// <summary>
    /// A <b>delete</b> carrying a precondition against an entity with no version column is refused, not
    /// silently served — the same ruling the update face already carries, on the verb where ignoring it is
    /// irrecoverable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A silently ignored <c>If-Match</c> is a lost update the caller believes it prevented, and on a delete
    /// the row it would have protected is gone. The ordinary delete in the same act is the counterweight: this
    /// cannot be implemented as "refuse every delete on a non-audited entity".
    /// </para>
    /// <para>
    /// <b>The message is asserted, and only for <c>audit</c>.</b> A stale precondition against a row that
    /// <em>does</em> carry a version is refused with the same exception and no message at all, so the type
    /// alone cannot tell "your version is old" from "this entity can never answer that question" — and only
    /// the second one names the descriptor flag that fixes it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_delete_precondition_against_an_entity_with_no_version_column_is_refused_not_ignored()
    {
        var host = await StartAsync();
        var caller = Caller();
        var row = (Guid)(await host.Data.CreateAsync(Entity, Payload("kept"), caller, cancellationToken: Ct))["id"]!;

        var refusal = await Should.ThrowAsync<AlvoPreconditionFailedException>(() => host.Data.DeleteAsync(
            Entity, row, caller, new AlvoPrecondition(DateTimeOffset.UnixEpoch), cancellationToken: Ct));

        refusal.Message.ShouldContain("audit", Case.Sensitive, "the fix is the descriptor flag, so it is named");
        (await host.Data.GetAsync(Entity, row, caller, Ct)).ShouldNotBeNull("the refused delete must not have landed");
        await host.Data.DeleteAsync(Entity, row, caller, cancellationToken: Ct);
        (await host.Data.GetAsync(Entity, row, caller, Ct)).ShouldBeNull("and the same delete without one still works");
    }

    /// <summary>
    /// A batch delete naming one row twice refuses every repeat under its own code, so a request layer can map
    /// "you sent this row twice" to something other than a policy denial.
    /// </summary>
    [Fact]
    public async Task A_batch_delete_naming_one_row_twice_carries_the_duplicate_row_code()
    {
        var host = await StartAsync();
        var caller = Caller();
        var row = (Guid)(await host.Data.CreateAsync(Entity, Payload("kept"), caller, cancellationToken: Ct))["id"]!;

        var result = await host.Data.DeleteManyAsync(Entity, [row, row], caller, cancellationToken: Ct);

        result.Refusals.ShouldHaveSingleItem().Code.ShouldBe("duplicate-row");
    }

    /// <summary>The three verbs that share <c>BatchDecision</c>, so a fourth cannot arrive without its guards.</summary>
    public static TheoryData<BatchVerb> Verbs() => [BatchVerb.Create, BatchVerb.Update, BatchVerb.Delete];

    /// <summary>Which batch member a theory case exercises.</summary>
    public enum BatchVerb
    {
        /// <summary><see cref="IAlvoData.CreateManyAsync"/>.</summary>
        Create,

        /// <summary><see cref="IAlvoData.UpdateManyAsync"/>.</summary>
        Update,

        /// <summary><see cref="IAlvoData.DeleteManyAsync"/>.</summary>
        Delete,
    }

    /// <summary>One batch carrying a single row, so only the guard under test can refuse it.</summary>
    private static Task<AlvoBatchResult> Invoke(
        AlvoDataHost host, BatchVerb verb, string entity, AlvoContext caller, AlvoIdempotency? token) =>
        verb switch
        {
            BatchVerb.Create => host.Data.CreateManyAsync(entity, [Payload("a")], caller, token, Ct),
            BatchVerb.Update => host.Data.UpdateManyAsync(
                entity, [new AlvoRowPatch(Guid.NewGuid(), Payload("a"))], caller, token, Ct),
            _ => host.Data.DeleteManyAsync(entity, [Guid.NewGuid()], caller, token, Ct),
        };

    /// <summary>The same batch carrying no rows at all.</summary>
    private static Task<AlvoBatchResult> InvokeEmpty(
        AlvoDataHost host, BatchVerb verb, string entity, AlvoContext caller) =>
        verb switch
        {
            BatchVerb.Create => host.Data.CreateManyAsync(entity, [], caller, cancellationToken: Ct),
            BatchVerb.Update => host.Data.UpdateManyAsync(entity, [], caller, cancellationToken: Ct),
            _ => host.Data.DeleteManyAsync(entity, [], caller, cancellationToken: Ct),
        };

    private Task<AlvoDataHost> StartAsync() => _fixture.StartAsync(Schema, Descriptor);

    private const string Entity = "notes";

    private const string Archives = "archives";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoIdempotency Token() => new($"key-{Guid.NewGuid():N}", "digest");

    private static Dictionary<string, object?> Payload(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title };

    private static AlvoContext Caller() => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

    private static AlvoDescriptor Descriptor => DescriptorFor(Entity, softDelete: false);

    private static AlvoDescriptor SoftDeleteDescriptor => DescriptorFor(Archives, softDelete: true);

    private static AlvoDescriptor DescriptorFor(string entity, bool softDelete) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "delete-guard-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [entity] = new()
            {
                Tenancy = EntityTenancy.Global,
                SoftDelete = softDelete,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String },
                },
                Rules = AllowAll,
            },
        },
    };

    private static SchemaModel Schema => new([EntityFor(Entity, softDelete: false)]);

    /// <summary>
    /// The same entity declaring <c>softDelete</c>, paired by hand: the core's mapper refuses that flag at
    /// apply, so the only schema that carries it is a host-assembled one — which is precisely the model this
    /// port's request-time belt exists for.
    /// </summary>
    private static SchemaModel SoftDeleteSchema => new([EntityFor(Archives, softDelete: true)]);

    private static EntitySchema EntityFor(string entity, bool softDelete)
    {
        List<FieldSchema> fields =
        [
            new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true, MaxLength = 100 },
        ];
        if (softDelete)
        {
            fields.Add(new FieldSchema
            {
                Name = AlvoManagedColumns.DeletedAt,
                Type = SchemaField.DateTime,
                Nullable = true,
            });
        }

        return new EntitySchema
        {
            Name = entity,
            Tenancy = TenancyMode.Global,
            SoftDelete = softDelete,
            Fields = fields,
        };
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
