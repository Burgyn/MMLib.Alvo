using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// What a <c>beforeDelete</c> hook does to a <b>batch</b> delete, and what the delete paths do with a patch no
/// delete can write.
/// </summary>
/// <remarks>
/// <para>
/// The batch face is the one whose absence is silent. A batch that never ran the hook, and a batch that ran it
/// and dropped the refusal on the floor, both answer <see cref="AlvoBatchResult.Succeeded"/> — the first
/// deletes every row the hook was declared to protect, the second deletes every row but that one and reports
/// nothing about it. Neither throws, and the single-row suite cannot see either.
/// </para>
/// <para>
/// The patch invariant needs a runner the descriptor cannot produce, because a <c>mutate</c> under
/// <c>beforeDelete</c> is refused when the descriptor is applied. Substituting the port is the only way to ask
/// what the write path does when the compiler and it disagree — which is exactly the situation the throw
/// exists for, and the one where staying silent would make the apply-time refusal quietly untrue.
/// </para>
/// </remarks>
public sealed class SqliteAlvoDataDeleteHookTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>
    /// A batch delete refuses the row its <c>beforeDelete</c> hook rejects, by that row's own request index,
    /// and removes nothing at all.
    /// </summary>
    [Fact]
    public async Task A_batch_delete_refuses_the_row_its_before_delete_hook_rejects()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var won = await CreateAsync(host, stage: Won);
        var open = await CreateAsync(host, stage: "open");

        var result = await host.Data.DeleteManyAsync(Deals, [won, open], Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse("a rejected row refuses the whole batch");
        result.Refusals.Select(refusal => refusal.Index).ShouldBe([0], ignoreOrder: false);
        // BOTH rows, and the rejected one is the half that was missing: "removes nothing at all" is
        // satisfied for `open` by an implementation that deletes `won` — the row the hook protected — and
        // that is the failure this case exists to catch, not the collateral one.
        (await host.Data.GetAsync(Deals, won, Caller, Ct)).ShouldNotBeNull("the rejected row must not be removed");
        (await host.Data.GetAsync(Deals, open, Caller, Ct)).ShouldNotBeNull("no row of a refused batch is removed");
    }

    /// <summary>
    /// The refusal carries the hook author's own <c>reject</c> text — descriptor-authored, and the one kind of
    /// text this framework echoes — rather than the generic "not available to this caller" every other refused
    /// row gets.
    /// </summary>
    /// <remarks>
    /// Only the author's own substring is asserted: the rest of the sentence names the hook's JSON pointer,
    /// which is the pipeline's to word and not this write path's to promise.
    /// </remarks>
    [Fact]
    public async Task A_rejected_rows_refusal_carries_the_hook_authors_own_text()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var won = await CreateAsync(host, stage: Won);

        var result = await host.Data.DeleteManyAsync(Deals, [won], Caller, cancellationToken: Ct);

        result.Refusals.ShouldHaveSingleItem().Message.ShouldContain(WonDeleteRefusal);
    }

    /// <summary>
    /// The counterweight: a row no hook rejects is removed by the same batch, so the refusal above cannot be
    /// satisfied by an implementation that refuses every batch delete.
    /// </summary>
    [Fact]
    public async Task A_row_no_hook_rejects_is_still_removed_by_the_batch()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var open = await CreateAsync(host, stage: "open");

        var result = await host.Data.DeleteManyAsync(Deals, [open], Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeTrue();
        (await host.Data.GetAsync(Deals, open, Caller, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// A <c>beforeDelete</c> hook that answers with a patch is an invariant violation on <b>both</b> delete
    /// faces, not a patch quietly dropped: a delete writes no payload, so a non-empty patch means the hook
    /// compiler and this write path disagree about what a delete may carry.
    /// </summary>
    /// <param name="batch">Whether the delete goes through the batch face.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_before_delete_hook_that_patches_is_an_invariant_violation(bool batch)
    {
        var host = await _fixture.StartAsync(
            Schema, Descriptor, configure: services => services.AddSingleton<IBeforeHookRunner>(new PatchingRunner()));
        var deal = await CreateAsync(host, stage: "open");

        await Should.ThrowAsync<InvalidOperationException>(() => batch
            ? host.Data.DeleteManyAsync(Deals, [deal], Caller, cancellationToken: Ct)
            : host.Data.DeleteAsync(Deals, deal, Caller, cancellationToken: Ct));
    }

    /// <summary>
    /// A runner that answers every delete with a patch — the disagreement the write path's throw exists for,
    /// which no descriptor can express because <c>mutate</c> under <c>beforeDelete</c> is refused at apply.
    /// </summary>
    private sealed class PatchingRunner : IBeforeHookRunner
    {
        public IReadOnlyDictionary<string, object?> Run(
            string entity,
            DataOperation operation,
            AlvoRecord candidate,
            AlvoRecord? previous,
            AlvoContext context,
            DateTimeOffset now) =>
            operation == DataOperation.Delete
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["stage"] = "patched" }
                : new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static async Task<Guid> CreateAsync(AlvoDataHost host, string stage) =>
        (Guid)(await host.Data.CreateAsync(
            Deals,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["stage"] = stage },
            Caller,
            cancellationToken: Ct))["id"]!;

    private const string Deals = "deals";

    private const string Won = "won";

    private const string WonDeleteRefusal = "A won deal is kept.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoContext Caller { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "delete-hook-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Deals] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["stage"] = new() { Type = DescField.String },
                },
                Rules = new AccessRules
                {
                    List = "true",
                    Get = "true",
                    Create = "true",
                    Update = "true",
                    Delete = "true",
                },
                Hooks = new EntityHooks
                {
                    BeforeDelete =
                    [
                        new BeforeHook
                        {
                            Condition = $"old.stage == '{Won}'",
                            Action = new BeforeHookAction { Reject = WonDeleteRefusal },
                        },
                    ],
                },
            },
        },
    };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Deals,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "stage", Type = SchemaField.String, Nullable = true, MaxLength = 40 },
            ],
        },
    ]);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
