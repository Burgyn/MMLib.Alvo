using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What one child write actually <b>issues</b>: the parent's lock, then one recompute per group, and the
/// refusals that stop a statement pair which could not be narrowed honestly from ever being issued.
/// </summary>
/// <remarks>
/// <para>
/// The statements are recorded and <em>suppressed</em> rather than run, so the pair, its order and its text are
/// asserted with no schema on disk. That is the whole difference from the engine suite
/// (<c>AlvoDataComputedRollupTests</c>), which reads the aggregated numbers back and therefore needs a real
/// database: a stored number proves the recompute happened, but it cannot show that the lock came first, that
/// two rollups following one foreign key were written in one statement, or that a mismatched pair was refused
/// before either statement left the process.
/// </para>
/// <para>
/// The two refusals are the belt against a <see cref="SchemaModel"/> that did not come through the descriptor
/// mapper — a host-assembled one, or F7's dynamic registry. Neither is reachable through a descriptor, so the
/// engine suite cannot reach them at all.
/// </para>
/// </remarks>
public class RollupRecomputeStatementTests
{
    private static Guid ParentId => Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static Guid TenantA => Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Guid TenantB => Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// The lock is taken <b>before</b> the recompute, which is the entire correctness argument: under READ
    /// COMMITTED the <c>SET</c> expression is evaluated from the snapshot taken at statement start, so a
    /// recompute that locks afterwards writes a stale aggregate — measured as 31 of 40 concurrent writers.
    /// </summary>
    [Fact]
    public async Task A_child_write_locks_the_parent_before_recomputing_it()
    {
        var statements = await StatementsAsync(Schema(), Child());

        statements.Count.ShouldBe(2);
        statements[0].ShouldStartWith("SELECT");
        statements[1].ShouldStartWith("UPDATE");
    }

    /// <summary>
    /// Every rollup following one foreign key is recomputed in <b>one</b> statement: the aggregates of one
    /// parent row are one fact about it, and a reader between two <c>UPDATE</c>s would see a <c>count</c> that
    /// does not match its <c>sum</c>.
    /// </summary>
    [Fact]
    public async Task Every_rollup_following_one_foreign_key_is_one_update()
    {
        var statements = await StatementsAsync(Schema(), Child());

        var update = statements[1];
        update.ShouldContain("""UPDATE "invoices" SET "net_total" = (""");
        update.ShouldContain(""", "item_count" = (""");
    }

    /// <summary>
    /// A scoped pair narrows <b>both</b> statements to the written child row's own tenant — the lock, so it
    /// cannot take another tenant's row, and the recompute, so it neither writes nor reads across the boundary.
    /// </summary>
    /// <remarks>
    /// The three assertions are positional on purpose, and the third is the one that matters. A single
    /// <c>ShouldAllBe(sql =&gt; sql.Contains("\"tenant_id\" = @p1"))</c> reads as if it covered both statements
    /// and does not: the recompute's own child predicate is <c>"invoice_items"."tenant_id" = @p1</c>, which
    /// CONTAINS that substring — so deleting the parent's <c>TenantPredicate</c> from the <c>UPDATE</c>'s own
    /// <c>WHERE</c> left the assertion green. That deletion is the cross-tenant write this class exists to
    /// prevent. Anchor each predicate to where it must appear instead.
    /// </remarks>
    [Fact]
    public async Task A_scoped_write_narrows_both_statements_to_the_tenant()
    {
        var statements = await StatementsAsync(Schema(TenancyMode.Scoped), Child(TenancyMode.Scoped), TenantA);

        statements.Count.ShouldBe(2);

        // The lock: one table source, so the predicate is unqualified, and the dialect's row-lock clause is
        // all that follows it.
        statements[0].ShouldEndWith("""WHERE "id" = @p0 AND "tenant_id" = @p1 FOR TEST""");

        // The recompute READ half — qualified, because the bare spelling would bind to the parent's column
        // inside the subquery and be true for every child row: a silent fail-open.
        statements[1].ShouldContain("\"invoice_items\".\"tenant_id\" = @p1");

        // The recompute WRITE half. Without this line the whole test survives the parent predicate's deletion.
        statements[1].ShouldEndWith("""WHERE "id" = @p0 AND "tenant_id" = @p1""");
    }

    /// <summary>
    /// A pair that disagrees about tenancy is refused <b>before either statement is issued</b>, and the refusal
    /// names both entities and which side is which — with only one side scoped there is no honest narrowing, and
    /// the silent alternative aggregates every tenant's children or writes another tenant's row.
    /// </summary>
    /// <remarks>
    /// Both orderings, because the guard is symmetric by construction today and the two have different failure
    /// modes if it is ever rewritten. The untested direction was the worse one: a global parent with a scoped
    /// child still narrows the child aggregate, so the defect shows up only as the parent <c>UPDATE</c>
    /// carrying no predicate at all.
    /// </remarks>
    /// <param name="parent">The parent's tenancy.</param>
    /// <param name="child">The child's, which disagrees with it.</param>
    /// <param name="tenants">
    /// The tenants the written child's images carry. A SCOPED child must carry exactly one, because
    /// <c>TenantOf</c> runs before the cross-tenancy guard and would otherwise refuse first — for the right
    /// reason, but not the one this case is about. A global child carries none.
    /// </param>
    [Theory]
    [InlineData(TenancyMode.Scoped, null, 0)]
    [InlineData(null, TenancyMode.Scoped, 1)]
    public async Task A_pair_that_disagrees_about_tenancy_is_refused(
        TenancyMode? parent, TenancyMode? child, int tenants)
    {
        var images = tenants == 0 ? [] : new[] { TenantA };

        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => StatementsAsync(Schema(parent), Child(child), images));

        // The contract is that the refusal names both entities and says which side carries a tenant. The
        // sentence that joins them is not contract: asserting "Entity 'invoices' rolls up 'invoice_items'"
        // would fail on a reworded message with no behaviour change, which is the wording-freeze this suite
        // declines to do everywhere else.
        refused.Message.ShouldContain("'invoices'");
        refused.Message.ShouldContain("'invoice_items'");
        refused.Message.ShouldContain("tenant-scoped");
        refused.Message.ShouldContain("global");
    }

    /// <summary>
    /// A scoped child whose images name more than one tenant is refused, naming the entity and the column: a
    /// stored row on a scoped entity always carries a tenant and can never move to another one, so this is an
    /// image that did not come off one stored row — and dropping the predicate instead is the cross-tenant write.
    /// </summary>
    [Fact]
    public async Task A_scoped_write_whose_images_name_two_tenants_is_refused()
    {
        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => StatementsAsync(Schema(TenancyMode.Scoped), Child(TenancyMode.Scoped), TenantA, TenantB));

        refused.Message.ShouldContain("'invoice_items'");
        refused.Message.ShouldContain(AlvoManagedColumns.TenantId);
    }

    /// <summary>An entity some other entity's field rolls up is one a write has to recompute for.</summary>
    [Fact]
    public void An_entity_something_rolls_up_is_recognised()
        => RollupRecompute.IsRolledUp(Schema(), Child()).ShouldBeTrue();

    /// <summary>
    /// And one nothing rolls up takes the fast path — without the counterweight the fact above is satisfied by
    /// an implementation that answers <see langword="true"/> for everything.
    /// </summary>
    [Fact]
    public void An_entity_nothing_rolls_up_is_not()
        => RollupRecompute.IsRolledUp(Schema(), Child() with { Name = "notes" }).ShouldBeFalse();

    /// <summary>
    /// Runs one child write against a context whose statements are recorded and suppressed, and hands back
    /// the statements in the order they were issued.
    /// </summary>
    /// <param name="schema">The applied schema the context is primed with.</param>
    /// <param name="child">The child entity that was written.</param>
    /// <param name="tenants">One <c>tenant_id</c> per row image; none at all for a global child.</param>
    private static async Task<IReadOnlyList<string>> StatementsAsync(
        SchemaModel schema, EntitySchema child, params Guid[] tenants)
    {
        var recorder = new StatementRecorder();
        await using var db = new AlvoDataContext(Options(recorder), schema, Guid.NewGuid());

        await new RollupRecompute(new TestSqlDialect(), new TestFieldSqlRenderer())
            .ForChildWriteAsync(db, child, Images(tenants), TestContext.Current.CancellationToken);

        return recorder.Statements;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Images(Guid[] tenants) =>
        tenants.Length == 0 ? [Image(tenant: null)] : [.. tenants.Select(tenant => Image(tenant))];

    private static Dictionary<string, object?> Image(Guid? tenant)
    {
        var image = new Dictionary<string, object?>(StringComparer.Ordinal) { ["invoice"] = ParentId };
        if (tenant is { } value)
        {
            image[AlvoManagedColumns.TenantId] = value;
        }

        return image;
    }

    private static DbContextOptions Options(StatementRecorder recorder) =>
        new DbContextOptionsBuilder()
            .UseSqlite("Data Source=:memory:")
            .AddInterceptors(recorder)
            .Options;

    private static SchemaModel Schema(TenancyMode? tenancy = null) => new([Parent(tenancy), Child(tenancy)]);

    private static EntitySchema Parent(TenancyMode? tenancy = null) => new()
    {
        Name = "invoices",
        Tenancy = tenancy,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "net_total", Type = FieldType.Decimal, Nullable = true, Rollup = Sum },
            new FieldSchema { Name = "item_count", Type = FieldType.Integer, Nullable = true, Rollup = Count },
        ],
    };

    private static EntitySchema Child(TenancyMode? tenancy = null) => new()
    {
        Name = "invoice_items",
        Tenancy = tenancy,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "invoice", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "line_total", Type = FieldType.Decimal, Nullable = true },
        ],
    };

    private static RollupSchema Sum => new()
    {
        From = "invoice_items",
        Via = "invoice",
        Op = RollupOperation.Sum,
        Field = "line_total",
    };

    private static RollupSchema Count =>
        new() { From = "invoice_items", Via = "invoice", Op = RollupOperation.Count };

    /// <summary>
    /// Records every statement the recompute issues and suppresses its execution, so the composed text can be
    /// asserted without a table to run it against.
    /// </summary>
    /// <remarks>
    /// An interceptor rather than <c>SqlCapture</c>'s diagnostic listener: suppression is what removes the
    /// database from these facts, and only an interceptor can refuse to run the command.
    /// </remarks>
    private sealed class StatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        internal IReadOnlyList<string> Statements => _statements;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            _statements.Add(command.CommandText.Trim());

            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }
}
