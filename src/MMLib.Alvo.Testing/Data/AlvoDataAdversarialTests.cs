using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The milestone's central security judgment: every <see cref="IAlvoData"/> implementation —
/// this package's own in-memory reference and, from PR2, real SQLite/PostgreSQL backends — must
/// satisfy every fact here identically. Written against the port before any storage existed, so a
/// provider cannot pass by accident; it must actually enforce <see cref="IPolicyEngine"/>'s
/// decision per row.
/// </summary>
/// <remarks>
/// <para>
/// Sixteen facts come from the F3 design brief; the rest were added because review or an earlier
/// task found the exact bug they guard against, so this suite proves the fix holds through the
/// full port, not only at the CEL-interpreter or policy-engine unit-test level — a bare boolean row
/// field in a rule, a <c>hidden</c> expression that cannot resolve for the caller, a write that
/// would place or move a row into another tenant, list-path field masking (not only single-row
/// masking), a payload rewriting a row's own <c>id</c>, a legitimate write that must actually
/// persist, and <c>Limit</c> applied after policy rather than before it. Every fact builds its own
/// descriptor, schema, and seed rows with freshly generated ids — no fact relies on another's data,
/// on insertion order, or on a fixed literal id.
/// </para>
/// <para>
/// <b>Per-fact isolation is required, not optional.</b> A subclass's <see cref="CreateAsync"/> must
/// return a store scoped to that one call — a fresh schema/table set, or at least data isolated
/// from every other fact's — never a single store shared and never reset across the whole suite.
/// Several facts (<c>logs</c>, <c>settings</c>, <c>posts</c>, <c>accounts</c>) declare no
/// row-scoping predicate at all (a bare <c>"true"</c> rule, or none) and assert an exact row
/// count; those assertions are only valid if no other fact's rows have ever landed in the same
/// entity. Facts may still run in any order or in parallel <em>relative to each other</em>, as
/// long as each owns its own isolated data.
/// </para>
/// </remarks>
public abstract class AlvoDataAdversarialTests
{
    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>,
    /// seeded with <paramref name="seed"/>'s rows. A real provider creates its physical schema from
    /// <paramref name="descriptor"/>/<paramref name="schema"/> and inserts <paramref name="seed"/>
    /// <b>out of band</b> — a raw <c>INSERT</c> (or equivalent), never through the port's own
    /// <see cref="IAlvoData.CreateAsync"/>: several fixtures seed rows a policy-respecting write
    /// could never produce (<c>notes</c> seeds rows for two different owners in one call; <c>logs</c>
    /// and <c>vaults</c> declare no <c>create</c> rule at all, so any policy-respecting insert into
    /// them would deny). Seeding therefore bypasses policy entirely, exactly like
    /// <c>InMemoryAlvoData.Seed</c> does.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules/tenancy/field flags apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>Alice sees her two rows, not Bob's.</summary>
    [Fact]
    public async Task List_returns_only_the_callers_own_rows()
    {
        var fixture = await NotesFixtureAsync();

        var result = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, fixture.Alice)).Items;

        result.Count.ShouldBe(2);
        result.ShouldAllBe(row => Equals(row["owner_id"], fixture.Alice.User.Value));
    }

    /// <summary><c>null</c>, not a 403.</summary>
    [Fact]
    public async Task Get_of_another_users_row_is_indistinguishable_from_absent()
    {
        var fixture = await NotesFixtureAsync();

        var result = await fixture.Data.GetAsync("notes", fixture.BobRowId, fixture.Alice);

        result.ShouldBeNull();
    }

    /// <summary><see cref="AlvoRecordNotFoundException"/>.</summary>
    [Fact]
    public async Task Update_of_another_users_row_reports_not_found()
    {
        var fixture = await NotesFixtureAsync();

        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => fixture.Data.UpdateAsync(
            "notes", fixture.BobRowId, new Dictionary<string, object?> { ["title"] = "hacked" }, fixture.Alice));
    }

    /// <summary>The row survives, verified as the owner afterwards.</summary>
    [Fact]
    public async Task Delete_of_another_users_row_reports_not_found_and_does_not_delete()
    {
        var fixture = await NotesFixtureAsync();

        await Should.ThrowAsync<AlvoRecordNotFoundException>(
            () => fixture.Data.DeleteAsync("notes", fixture.BobRowId, fixture.Alice));

        var survived = await fixture.Data.GetAsync("notes", fixture.BobRowId, fixture.Bob);
        survived.ShouldNotBeNull();
    }

    /// <summary><c>owner_id == @user.id</c> with a payload naming Bob → <see cref="AlvoAuthorizationException"/>.</summary>
    [Fact]
    public async Task Create_that_would_place_the_row_outside_the_callers_scope_is_denied()
    {
        var fixture = await NotesFixtureAsync();
        var payload = new Dictionary<string, object?>
        {
            ["owner_id"] = fixture.Bob.User.Value,
            ["tenant_id"] = fixture.Tenant.Value,
            ["title"] = "smuggled",
        };

        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.CreateAsync("notes", payload, fixture.Alice));
    }

    /// <summary>The <c>WITH CHECK</c> half: Alice updating her own row to <c>owner_id = Bob</c> is denied and the stored row is unchanged.</summary>
    [Fact]
    public async Task Update_cannot_move_a_row_out_of_the_callers_scope()
    {
        var fixture = await NotesFixtureAsync();
        var payload = new Dictionary<string, object?> { ["owner_id"] = fixture.Bob.User.Value };

        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => fixture.Data.UpdateAsync("notes", fixture.AliceRow1Id, payload, fixture.Alice));

        var stillHers = await fixture.Data.GetAsync("notes", fixture.AliceRow1Id, fixture.Alice);
        stillHers.ShouldNotBeNull();
        stillHers!["owner_id"].ShouldBe(fixture.Alice.User.Value);
    }

    /// <summary>No <c>rules</c> block at all: all five operations throw.</summary>
    [Fact]
    public async Task An_entity_with_no_rule_denies_every_operation()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        var (descriptor, schema) = BuildFixture("vaults", fields, EntityTenancy.Global, rules: null);
        var data = await CreateAsync(schema, descriptor, EmptySeed());
        var caller = NewContext(tenant: null);
        var randomId = Guid.NewGuid();
        var payload = new Dictionary<string, object?> { ["title"] = "x" };

        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.QueryAsync(new AlvoQuery { Entity = "vaults" }, caller));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.GetAsync("vaults", randomId, caller));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.CreateAsync("vaults", payload, caller));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.UpdateAsync("vaults", randomId, payload, caller));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.DeleteAsync("vaults", randomId, caller));
    }

    /// <summary><c>list</c> allowed, <c>delete</c> denied, on the same entity.</summary>
    [Fact]
    public async Task An_operation_with_no_rule_denies_while_its_siblings_work()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "true" };
        var (descriptor, schema) = BuildFixture("logs", fields, EntityTenancy.Global, rules);
        var seed = SeedOf("logs", Row(Guid.NewGuid(), ("title", "entry")));
        var data = await CreateAsync(schema, descriptor, seed);
        var caller = NewContext(tenant: null);

        var listed = (await data.QueryAsync(new AlvoQuery { Entity = "logs" }, caller)).Items;
        listed.Count.ShouldBe(1);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.DeleteAsync("logs", Guid.NewGuid(), caller));
    }

    /// <summary>Acme's caller cannot see Globex's rows even with a permissive <c>"true"</c> rule.</summary>
    [Fact]
    public async Task A_tenant_scoped_entity_never_returns_another_tenants_rows()
    {
        var fixture = await DocumentsFixtureAsync();
        var acmeUser = NewContext(fixture.Acme);

        var result = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "documents" }, acmeUser)).Items;

        result.Count.ShouldBe(1);
        result[0]["tenant_id"].ShouldBe(fixture.Acme.Value);
    }

    /// <summary>
    /// <b>The count is an aggregate over rows, which is exactly what makes it a disclosure channel.</b> A
    /// caller learns nothing from an empty page, but "there are 3 rows" over a set they can see one of tells
    /// them two rows exist somewhere they cannot read. So the count is composed over the <em>same</em>
    /// <c>WHERE</c> terms as the page — the resolved <c>USING</c> predicate and the synthesized tenant scope
    /// included — and every tenant here counts exactly its own row over a store holding three.
    /// </summary>
    /// <remarks>
    /// A count taken over the bare table would return 3 to all three callers while every row-level fact in
    /// this suite kept passing: no row crosses a tenant boundary, only the number does. That is the shape of
    /// leak an outcome-level suite cannot see, which is why this is a fact of its own rather than an
    /// assertion bolted onto the paging suite's count facts, whose fixture is single-tenant.
    /// </remarks>
    [Fact]
    public async Task A_count_is_taken_over_the_policy_filtered_set_and_never_over_the_table()
    {
        var fixture = await DocumentsFixtureAsync();
        var counted = new List<long?>();

        foreach (var tenant in new[] { fixture.Acme, fixture.Globex, fixture.Third })
        {
            var page = await fixture.Data.QueryAsync(
                new AlvoQuery { Entity = "documents", IncludeTotalCount = true }, NewContext(tenant));
            counted.Add(page.TotalCount);
        }

        counted.ShouldAllBe(total => total == 1);
    }

    /// <summary>
    /// The row-rule half of the same claim, on a fixture whose visible subset is decided by a
    /// <c>USING</c> predicate rather than by tenancy: Alice owns two of the three <c>notes</c> rows, so her
    /// count is 2 and never 3.
    /// </summary>
    [Fact]
    public async Task A_count_is_narrowed_by_the_row_rule_and_by_the_callers_own_filter()
    {
        var fixture = await NotesFixtureAsync();

        var byRule = await fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", IncludeTotalCount = true }, fixture.Alice);
        var byFilter = await fixture.Data.QueryAsync(
            new AlvoQuery
            {
                Entity = "notes",
                Filter = new AlvoComparison("title", AlvoFilterOperator.Eq, "Alice-1"),
                IncludeTotalCount = true,
            },
            fixture.Alice);

        byRule.TotalCount.ShouldBe(2);
        byFilter.TotalCount.ShouldBe(1);
    }

    /// <summary>
    /// A count over a field the caller may not read is refused exactly as the read itself is — the count adds
    /// no second path into the query, so a filter naming a hidden field cannot become a counting oracle that
    /// answers where the page refuses.
    /// </summary>
    /// <remarks>
    /// <b>Over a genuinely <c>hidden</c> field, and over an undeclared one, because they are two branches of
    /// one guard and only the first is about confidentiality.</b> An earlier revision of this fact asked only
    /// the undeclared name, which pins the wrong branch: it would still have passed had the masked branch
    /// been broken for a counted read. The counting oracle is the sharper of the two — a caller who cannot
    /// read <c>secret</c> but can learn how many rows carry a given value of it has the field one comparison
    /// at a time, without a single row ever appearing in a response. The admin arm is the control: the field
    /// is refused because it is hidden <em>from this caller</em>, not because counting over it is banned.
    /// </remarks>
    [Fact]
    public async Task A_count_over_a_hidden_field_filter_is_refused_like_the_read_itself()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);
        var admin = NewContext(tenant: null, Role.Admin);
        var byNote = CountedQueryFilteredBy(new AlvoComparison("note", AlvoFilterOperator.Eq, "internal"));

        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(
            CountedQueryFilteredBy(new AlvoComparison("secret", AlvoFilterOperator.Eq, "shh")), member));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(byNote, member));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(
            CountedQueryFilteredBy(new AlvoComparison("nosuchfield", AlvoFilterOperator.Eq, "x")), member));

        // `note` is hidden by an expression that admits an admin, so the same counted query answers for one
        // — the refusal above is per-caller masking, not a ban on counting over that field.
        (await fixture.Data.QueryAsync(byNote, admin)).TotalCount.ShouldBe(1);
    }

    /// <inheritdoc cref="QueryFilteredBy"/>
    private static AlvoQuery CountedQueryFilteredBy(AlvoFilter filter) =>
        QueryFilteredBy(filter) with { IncludeTotalCount = true };

    /// <summary>
    /// The §4 acceptance criterion, made into a real, independently failing assertion: a tenantless
    /// caller's query throws with no rows ever assigned to the caller's variable (an implementation
    /// that throws only after materializing the rows would still fail this), <b>and</b> no
    /// tenant-bearing caller over the very same store — Acme, Globex, or a third tenant — ever sees
    /// more than exactly its own one row, proving the throw is not masking a leak everyone else
    /// still gets.
    /// </summary>
    [Fact]
    public async Task A_query_with_no_tenant_context_fails_rather_than_returning_every_tenants_rows()
    {
        var fixture = await DocumentsFixtureAsync();
        var tenantless = NewContext(tenant: null);

        IReadOnlyList<AlvoRecord>? captured = null;
        await Should.ThrowAsync<AlvoAuthorizationException>(async () =>
            captured = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "documents" }, tenantless)).Items);

        captured.ShouldBeNull();

        var acmeResult = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "documents" }, NewContext(fixture.Acme))).Items;
        var globexResult = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "documents" }, NewContext(fixture.Globex))).Items;
        var thirdResult = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "documents" }, NewContext(fixture.Third))).Items;

        acmeResult.Count.ShouldBe(1);
        globexResult.Count.ShouldBe(1);
        thirdResult.Count.ShouldBe(1);
    }

    /// <summary>
    /// Design <em>Verification</em> §4: a query issued with <see langword="null"/> for
    /// <see cref="AlvoContext"/> throws, rather than defaulting to anonymous or to every tenant's rows
    /// — a caller identity is required before any policy can even be resolved. Distinct from
    /// <see cref="A_query_with_no_tenant_context_fails_rather_than_returning_every_tenants_rows"/>: that
    /// fact is a caller who <b>has</b> an identity but no tenant, denied by the tenant guard; this one
    /// has no <see cref="AlvoContext"/> at all, caught by <c>ArgumentNullException.ThrowIfNull(context)</c>
    /// at the top of every <c>EfAlvoData</c> member — one piece of shared code both EF drivers call
    /// through, so there is no engine-specific path that could diverge.
    /// </summary>
    [Fact]
    public async Task A_query_with_no_context_at_all_throws_rather_than_defaulting_to_anyone()
    {
        var fixture = await NotesFixtureAsync();

        await Should.ThrowAsync<ArgumentNullException>(
            () => fixture.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, null!));
        await Should.ThrowAsync<ArgumentNullException>(
            () => fixture.Data.GetAsync("notes", fixture.AliceRow1Id, null!));
    }

    /// <summary>A tenantless context cannot create into a scoped entity, even with a permissive <c>"true"</c> rule.</summary>
    [Fact]
    public async Task A_tenantless_context_cannot_create_into_a_scoped_entity()
    {
        var fixture = await DocumentsFixtureAsync();
        var tenantless = NewContext(tenant: null);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.CreateAsync(
            "documents", new Dictionary<string, object?> { ["title"] = "x" }, tenantless));
    }

    /// <summary>No id-probing oracle: a cross-tenant <c>get</c> by id is indistinguishable from absent.</summary>
    [Fact]
    public async Task Cross_tenant_get_by_id_is_indistinguishable_from_absent()
    {
        var fixture = await DocumentsFixtureAsync();
        var acmeUser = NewContext(fixture.Acme);

        var result = await fixture.Data.GetAsync("documents", fixture.GlobexRowId, acmeUser);

        result.ShouldBeNull();
    }

    /// <summary>
    /// Both a statically <c>hidden: true</c> field and a context-conditional one never appear in a
    /// returned record — on every path that returns one: a single-row read, a list of many rows
    /// (masking every row, not only the first or only a single-row read), and the record a write
    /// itself returns.
    /// </summary>
    [Fact]
    public async Task A_hidden_field_never_appears_in_a_returned_record()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);
        var admin = NewContext(tenant: null, Role.Admin);

        var asMember = await fixture.Data.GetAsync("accounts", fixture.RowId, member);
        asMember.ShouldNotBeNull();
        asMember!.Values.ContainsKey("secret").ShouldBeFalse();
        asMember.Values.ContainsKey("note").ShouldBeFalse();

        var asAdmin = await fixture.Data.GetAsync("accounts", fixture.RowId, admin);
        asAdmin.ShouldNotBeNull();
        asAdmin!.Values.ContainsKey("secret").ShouldBeFalse();
        asAdmin.Values.ContainsKey("note").ShouldBeTrue();

        var listedByMember = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "accounts" }, member)).Items;
        listedByMember.Count.ShouldBe(2);
        listedByMember.ShouldAllBe(row => !row.Values.ContainsKey("secret") && !row.Values.ContainsKey("note"));

        var updated = await fixture.Data.UpdateAsync(
            "accounts", fixture.RowId, new Dictionary<string, object?> { ["title"] = "Renamed" }, member);
        updated.Values.ContainsKey("secret").ShouldBeFalse();
        updated.Values.ContainsKey("note").ShouldBeFalse();
    }

    /// <summary>Rejected, never silently dropped — and the stored value is unchanged.</summary>
    [Fact]
    public async Task A_write_to_a_read_only_field_is_rejected_rather_than_silently_dropped()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);

        var ex = await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.UpdateAsync(
            "accounts", fixture.RowId, new Dictionary<string, object?> { ["status"] = "closed" }, member));
        ex.Message.ShouldContain("status");

        var unchanged = await fixture.Data.GetAsync("accounts", fixture.RowId, member);
        unchanged.ShouldNotBeNull();
        unchanged!["status"].ShouldBe("active");
    }

    /// <summary>
    /// The regression that justifies <c>@user.roles</c>: a caller holding {authenticated, admin}
    /// satisfies both rules. Also carries the negative leg a broken, always-<see langword="true"/>
    /// <c>in</c> could otherwise hide behind: a plain authenticated, non-admin caller must see
    /// <c>list</c> filtered down to nothing (the row-visibility predicate genuinely excludes it)
    /// while <c>get</c> — gated only on <c>'authenticated' in @user.roles</c> — still works for the
    /// very same caller, proving role membership is actually being evaluated, not merely allowed
    /// through unconditionally.
    /// </summary>
    [Fact]
    public async Task An_admin_rule_over_the_role_set_matches_a_multi_role_caller()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "'admin' in @user.roles", Get = "'authenticated' in @user.roles" };
        var (descriptor, schema) = BuildFixture("settings", fields, EntityTenancy.Global, rules);
        var rowId = Guid.NewGuid();
        var seed = SeedOf("settings", Row(rowId, ("title", "Config")));
        var data = await CreateAsync(schema, descriptor, seed);
        var multiRole = NewContext(tenant: null, Role.Admin);
        var nonAdmin = NewContext(tenant: null);

        var listed = (await data.QueryAsync(new AlvoQuery { Entity = "settings" }, multiRole)).Items;
        listed.Count.ShouldBe(1);

        var got = await data.GetAsync("settings", rowId, multiRole);
        got.ShouldNotBeNull();

        var listedByNonAdmin = (await data.QueryAsync(new AlvoQuery { Entity = "settings" }, nonAdmin)).Items;
        listedByNonAdmin.ShouldBeEmpty();

        var gotByNonAdmin = await data.GetAsync("settings", rowId, nonAdmin);
        gotByNonAdmin.ShouldNotBeNull();
    }

    /// <summary>A caller-supplied <c>owner_id = &lt;Bob&gt;</c> filter returns nothing rather than Bob's rows.</summary>
    [Fact]
    public async Task A_user_filter_cannot_widen_the_policy_predicate()
    {
        var fixture = await NotesFixtureAsync();
        var query = new AlvoQuery
        {
            Entity = "notes",
            Filter = new AlvoComparison("owner_id", AlvoFilterOperator.Eq, fixture.Bob.User.Value),
        };

        var result = (await fixture.Data.QueryAsync(query, fixture.Alice)).Items;

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// A nullable boolean row field used bare (not compared) in a rule works end to end — this
    /// shape compiled but threw at request time until Task 9's fix.
    /// </summary>
    [Fact]
    public async Task A_nullable_boolean_field_used_bare_in_a_rule_works_end_to_end()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = DescField.Uuid, Required = true },
            ["is_public"] = new() { Type = DescField.Boolean },
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "is_public || owner_id == @user.id" };
        var (descriptor, schema) = BuildFixture("posts", fields, EntityTenancy.Global, rules);

        var alice = NewContext(tenant: null);
        var bob = NewContext(tenant: null);
        var publicRowId = Guid.NewGuid();
        var aliceOwnRowId = Guid.NewGuid();
        var bobPrivateRowId = Guid.NewGuid();

        var seed = SeedOf(
            "posts",
            Row(publicRowId, ("owner_id", bob.User.Value), ("is_public", true), ("title", "Public")),
            Row(aliceOwnRowId, ("owner_id", alice.User.Value), ("is_public", null), ("title", "Alice-private")),
            Row(bobPrivateRowId, ("owner_id", bob.User.Value), ("is_public", null), ("title", "Bob-private")));
        var data = await CreateAsync(schema, descriptor, seed);

        var visible = (await data.QueryAsync(new AlvoQuery { Entity = "posts" }, alice)).Items;

        visible.Count.ShouldBe(2);
        visible.ShouldContain(row => Equals(row["id"], publicRowId));
        visible.ShouldContain(row => Equals(row["id"], aliceOwnRowId));
        visible.ShouldNotContain(row => Equals(row["id"], bobPrivateRowId));
    }

    /// <summary>
    /// A <c>hidden</c> expression whose evaluation cannot resolve for the caller (comparing
    /// <c>@tenant.id</c> for a tenantless caller, on a <c>Global</c> entity so the tenant guard
    /// does not intervene first) still masks the field — masks fail closed. This was a live
    /// fail-open until Task 11's fix.
    /// </summary>
    [Fact]
    public async Task A_hidden_expression_that_cannot_resolve_still_masks_the_field()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
            ["sensitive"] = new() { Type = DescField.String, Hidden = BoolOrCel.FromExpression("!(@tenant.id == @user.id)") },
        };
        var rules = new AccessRules { List = "true", Get = "true" };
        var (descriptor, schema) = BuildFixture("globalItems", fields, EntityTenancy.Global, rules);
        var rowId = Guid.NewGuid();
        var seed = SeedOf("globalItems", Row(rowId, ("title", "x"), ("sensitive", "y")));
        var data = await CreateAsync(schema, descriptor, seed);
        var tenantless = NewContext(tenant: null);

        var result = await data.GetAsync("globalItems", rowId, tenantless);

        result.ShouldNotBeNull();
        result!.Values.ContainsKey("sensitive").ShouldBeFalse();
    }

    /// <summary>
    /// The write-side mirror of <see cref="A_tenant_scoped_entity_never_returns_another_tenants_rows"/>:
    /// a permissive <c>"true"</c> create rule is not enough to place a row in another tenant. Only
    /// the synthesized tenant scope, evaluated over the create post-image, can deny this — an
    /// implementation that renders the tenant scope into the read <c>WHERE</c> but omits it from
    /// the write-side <c>WITH CHECK</c> evaluation would otherwise let an Acme caller create rows
    /// directly into Globex's tenant.
    /// </summary>
    [Fact]
    public async Task Create_into_another_tenant_is_denied()
    {
        var fixture = await DocumentsFixtureAsync();
        var acmeUser = NewContext(fixture.Acme);
        var payload = new Dictionary<string, object?> { ["tenant_id"] = fixture.Globex.Value, ["title"] = "smuggled" };

        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.CreateAsync("documents", payload, acmeUser));
    }

    /// <summary>
    /// The update-side mirror: a caller cannot move an existing row into another tenant either.
    /// <c>tenant_id</c> is a framework-managed column a payload may never touch on update at all
    /// (see <see cref="Update_cannot_rewrite_the_row_id"/>'s sibling check), so this is denied
    /// before any rule even runs — and the stored row is confirmed unchanged afterward.
    /// </summary>
    [Fact]
    public async Task Update_cannot_move_a_row_into_another_tenant()
    {
        var fixture = await NotesFixtureAsync();
        var otherTenant = TenantId.New();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.UpdateAsync(
            "notes", fixture.AliceRow1Id, new Dictionary<string, object?> { ["tenant_id"] = otherTenant.Value }, fixture.Alice));

        var stillInOriginalTenant = await fixture.Data.GetAsync("notes", fixture.AliceRow1Id, fixture.Alice);
        stillInOriginalTenant.ShouldNotBeNull();
        stillInOriginalTenant!["tenant_id"].ShouldBe(fixture.Tenant.Value);
    }

    /// <summary>
    /// <c>id</c> is assigned once, at creation, and a payload can never rewrite it — an
    /// implementation that only checks a payload key against the policy's descriptor-declared
    /// <c>ReadOnlyFields</c> would miss this entirely, since <c>id</c> is a framework-managed column
    /// injected by schema mapping, never a descriptor field, so it can never appear in that set. A
    /// port that let this through would corrupt row identity — two rows sharing one <c>id</c>, and
    /// the row whose id was stolen becoming unreachable.
    /// </summary>
    [Fact]
    public async Task Update_cannot_rewrite_the_row_id()
    {
        var fixture = await NotesFixtureAsync();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.UpdateAsync(
            "notes", fixture.AliceRow1Id, new Dictionary<string, object?> { ["id"] = fixture.BobRowId }, fixture.Alice));

        var aliceRowIntact = await fixture.Data.GetAsync("notes", fixture.AliceRow1Id, fixture.Alice);
        aliceRowIntact.ShouldNotBeNull();

        var bobRowIntact = await fixture.Data.GetAsync("notes", fixture.BobRowId, fixture.Bob);
        bobRowIntact.ShouldNotBeNull();
    }

    /// <summary>
    /// The distinguishing case between post-image and payload-only evaluation: a payload that
    /// touches only an unrelated field (never mentioning <c>owner_id</c>) must still succeed,
    /// because the complete post-image (stored row merged with the payload) still satisfies
    /// <c>owner_id == @user.id</c> — an implementation that evaluated <c>WITH CHECK</c> against the
    /// payload alone would see <c>owner_id</c> as absent (reading as <see langword="null"/>) and
    /// wrongly deny every such update.
    /// </summary>
    [Fact]
    public async Task Update_of_an_unrelated_field_succeeds_when_the_post_image_still_satisfies_the_rule()
    {
        var fixture = await NotesFixtureAsync();

        var updated = await fixture.Data.UpdateAsync(
            "notes", fixture.AliceRow1Id, new Dictionary<string, object?> { ["title"] = "renamed" }, fixture.Alice);

        updated["title"].ShouldBe("renamed");
        updated["owner_id"].ShouldBe(fixture.Alice.User.Value);

        var reread = await fixture.Data.GetAsync("notes", fixture.AliceRow1Id, fixture.Alice);
        reread.ShouldNotBeNull();
        reread!["title"].ShouldBe("renamed");
    }

    /// <summary>
    /// A legitimate create must actually persist and be subsequently readable — otherwise an
    /// implementation whose writes silently never land would still pass every other fact in this
    /// suite (they all assert what a caller cannot do, never that an allowed write took effect).
    /// </summary>
    [Fact]
    public async Task Create_of_an_allowed_row_persists_and_is_subsequently_readable()
    {
        var fixture = await NotesFixtureAsync();
        var payload = new Dictionary<string, object?>
        {
            ["owner_id"] = fixture.Alice.User.Value,
            ["tenant_id"] = fixture.Tenant.Value,
            ["title"] = "brand new",
        };

        var created = await fixture.Data.CreateAsync("notes", payload, fixture.Alice);
        created["title"].ShouldBe("brand new");
        var createdId = (Guid)created["id"]!;

        var reread = await fixture.Data.GetAsync("notes", createdId, fixture.Alice);
        reread.ShouldNotBeNull();
        reread!["title"].ShouldBe("brand new");
    }

    /// <summary>
    /// DoD #5's storage half: the CsCheck property suites (<c>NoInterpolationPropertyTests</c>,
    /// <c>FilterSqlRendererPropertyTests</c>) prove unicode never breaks out of a bound parameter at
    /// the <em>renderer</em> level, but that says nothing about whether the <em>value</em> survives a
    /// real column's storage and retrieval. A code point outside the Basic Multilingual Plane (a
    /// UTF-16 surrogate pair, a four-byte UTF-8 sequence) and a base letter followed by a combining
    /// mark rather than its precomposed form are exactly where a naive <c>TEXT</c>/<c>varchar</c>
    /// round trip silently truncates or normalizes — a column that only ever saw ASCII would still
    /// look correct.
    /// </summary>
    /// <param name="value">The non-ASCII value written and expected to read back unchanged.</param>
    [Theory]
    [InlineData("\U0001F600 grinning face")] // outside the BMP: a UTF-16 surrogate pair, 4-byte UTF-8
    [InlineData("café")] // 'e' + COMBINING ACUTE ACCENT (U+0301), not the precomposed 'é' (U+00E9)
    public async Task A_non_ascii_value_survives_a_create_and_get_round_trip_unchanged(string value)
    {
        var fixture = await NotesFixtureAsync();
        var payload = new Dictionary<string, object?>
        {
            ["owner_id"] = fixture.Alice.User.Value,
            ["tenant_id"] = fixture.Tenant.Value,
            ["title"] = value,
        };

        var created = await fixture.Data.CreateAsync("notes", payload, fixture.Alice);
        created["title"].ShouldBe(value);

        var reread = await fixture.Data.GetAsync("notes", (Guid)created["id"]!, fixture.Alice);
        reread.ShouldNotBeNull();
        reread!["title"].ShouldBe(value);
    }

    /// <summary>
    /// <c>Limit</c> must be applied after the policy predicate (and the tenant scope), never
    /// before — a <c>Limit</c> that truncated the pre-filter row set could return another tenant's
    /// row from the head of the table just because the caller's own row landed later, entirely
    /// independent of any predicate bug. Acme's caller asks for at most one row and must still get
    /// exactly its own, never Globex's or the third tenant's.
    /// </summary>
    /// <remarks>
    /// The descending <c>Sort</c> is what makes this fact deterministic rather than a coin flip:
    /// the seed order puts Acme's row first, so an implementation that truncated before filtering
    /// would happen to return the right row anyway. Sorted by <c>title</c> descending, Acme's row
    /// (<c>Acme-doc</c>) is the <em>last</em> of the three, so a limit applied to the pre-filter row
    /// set returns another tenant's row, which the policy then strips — an empty page, not this one.
    /// </remarks>
    [Fact]
    public async Task A_query_limit_is_applied_after_the_policy_predicate_not_before()
    {
        var fixture = await DocumentsFixtureAsync();
        var acmeUser = NewContext(fixture.Acme);

        var query = new AlvoQuery
        {
            Entity = "documents",
            Limit = 1,
            Sort = [new AlvoSort("title", Descending: true)],
        };
        var result = (await fixture.Data.QueryAsync(query, acmeUser)).Items;

        result.Count.ShouldBe(1);
        result[0]["id"].ShouldBe(fixture.AcmeRowId);
    }

    /// <summary>
    /// A <c>hidden</c> field is not merely stripped from the response — it must not be usable as a
    /// filter operand at all. <see cref="IAlvoData.QueryAsync"/> filters and pages the <b>raw</b> row
    /// and masks only on the way out, so a permitted <c>secret.gt.&lt;x&gt;</c> filter would let a
    /// caller binary-search a value they may never read, one request per bit. Masking fails closed, so
    /// the query is refused rather than answered — including when the field is buried inside a nested
    /// <c>not(and(...))</c>, and including a field whose <c>hidden</c> expression only resolves to
    /// hidden for <em>this</em> caller (the same filter must still work for the admin it is visible to,
    /// so the refusal is really per-caller masking and not a blanket rejection of the field name).
    /// </summary>
    [Fact]
    public async Task A_filter_naming_a_hidden_field_is_rejected_rather_than_answered()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);
        var admin = NewContext(tenant: null, Role.Admin);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(
            QueryFilteredBy(new AlvoComparison("secret", AlvoFilterOperator.Gt, "m")), member));

        var nested = new AlvoNot(new AlvoAnd([
            new AlvoComparison("title", AlvoFilterOperator.Eq, "Acct"),
            new AlvoComparison("secret", AlvoFilterOperator.Eq, "shh"),
        ]));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(QueryFilteredBy(nested), member));

        var byNote = QueryFilteredBy(new AlvoComparison("note", AlvoFilterOperator.Eq, "internal"));
        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(byNote, member));

        var asAdmin = (await fixture.Data.QueryAsync(byNote, admin)).Items;
        asAdmin.Count.ShouldBe(1);
        asAdmin[0]["id"].ShouldBe(fixture.RowId);
    }

    /// <summary>
    /// The sort channel leaks the same secret more cheaply: ordering by a hidden field discloses its
    /// relative ordering across every returned row in a single request, with no value ever appearing
    /// in the response. Refused for the same reason, and the visible sibling field still sorts.
    /// </summary>
    [Fact]
    public async Task A_sort_naming_a_hidden_field_is_rejected_rather_than_leaking_its_ordering()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);

        var byHidden = new AlvoQuery { Entity = "accounts", Sort = [new AlvoSort("secret", Descending: true)] };
        await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(byHidden, member));

        var byVisible = new AlvoQuery { Entity = "accounts", Sort = [new AlvoSort("title", Descending: true)] };
        var sorted = (await fixture.Data.QueryAsync(byVisible, member)).Items;
        sorted.Count.ShouldBe(2);
        sorted[0]["title"].ShouldBe("Acct-2");
    }

    /// <summary>
    /// A filter/sort field name is the one caller-supplied string a real backend interpolates into
    /// <c>WHERE</c>/<c>ORDER BY</c> as an <em>identifier</em> — SQL has no bind-parameter form of a
    /// column name — so it must be validated against the entity's declared fields at this port, before
    /// it can ever reach that seam. The name used here is a quote-breaking payload; the refusal must
    /// also not echo it back, since it is attacker-controlled text and a log-injection vector, and must
    /// be indistinguishable from the refusal a merely-hidden field gets (otherwise the pair of messages
    /// is itself a schema-shape oracle).
    /// </summary>
    [Fact]
    public async Task A_filter_or_sort_naming_a_field_the_schema_does_not_declare_is_rejected()
    {
        const string InjectionAttempt = "title\"; DROP TABLE items; --";
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);

        var filtered = await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(
            QueryFilteredBy(new AlvoComparison(InjectionAttempt, AlvoFilterOperator.Eq, "x")), member));
        filtered.Message.ShouldNotContain("DROP TABLE");

        var sorted = await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "accounts", Sort = [new AlvoSort(InjectionAttempt)] }, member));
        sorted.Message.ShouldNotContain("DROP TABLE");

        var hidden = await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.QueryAsync(
            QueryFilteredBy(new AlvoComparison("secret", AlvoFilterOperator.Eq, "shh")), member));
        filtered.Message.ShouldBe(hidden.Message);
    }

    /// <summary>
    /// A caller-built filter tree is walked recursively by every backend that renders or evaluates it,
    /// so an unbounded one is a denial-of-service against the process itself — a
    /// <c>StackOverflowException</c> no <c>catch</c> can contain. The cap is enforced at the port, at
    /// the boundary: exactly <see cref="AlvoFilter.MaxDepth"/> is answered normally, one level more is
    /// rejected, and a tree far past any stack budget is still only a rejection.
    /// </summary>
    [Fact]
    public async Task A_filter_tree_deeper_than_the_cap_is_rejected_rather_than_walked()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);

        var atCap = (await fixture.Data.QueryAsync(QueryFilteredBy(NestedFilter(AlvoFilter.MaxDepth)), member)).Items;
        atCap.ShouldNotBeNull();

        await Should.ThrowAsync<ArgumentException>(() => fixture.Data.QueryAsync(
            QueryFilteredBy(NestedFilter(AlvoFilter.MaxDepth + 1)), member));

        await Should.ThrowAsync<ArgumentException>(() => fixture.Data.QueryAsync(
            QueryFilteredBy(NestedFilter(50_000)), member));
    }

    /// <summary>
    /// <c>softDelete</c> is declared in the frozen descriptor schema — "DELETE becomes a soft delete, and
    /// reads/list/get/rollup auto-exclude soft-deleted rows" — and is <b>not implemented</b>. Measured on real
    /// PostgreSQL: <see cref="IAlvoData.DeleteAsync"/> removed the row outright, and a row whose
    /// <c>deleted_at</c> was set was still listed. So a delete on such an entity is refused, loudly, rather
    /// than silently destroying data the contract promises is recoverable.
    /// </summary>
    /// <remarks>
    /// A rule of the port, so it is here: a reference implementation that quietly hard-deleted where a shipped
    /// backend refuses would give the port two contracts. The refusal is
    /// <see cref="InvalidOperationException"/> — not a denial and not a malformed query, but a schema this
    /// port cannot serve. The counterweight is in the same act: an ordinary entity's delete still works, so
    /// the refusal cannot be satisfied by refusing every delete.
    /// </remarks>
    [Fact]
    public async Task A_delete_on_a_soft_delete_entity_is_refused_rather_than_hard_deleting()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "true", Get = "true", Delete = "true" };
        var (descriptor, schema) = BuildFixture("archives", fields, EntityTenancy.Global, rules, softDelete: true);
        var rowId = Guid.NewGuid();
        var data = await CreateAsync(schema, descriptor, SeedOf("archives", Row(rowId, ("title", "Kept"))));
        var caller = NewContext(tenant: null);

        await Should.ThrowAsync<InvalidOperationException>(() => data.DeleteAsync("archives", rowId, caller));

        var survived = await data.GetAsync("archives", rowId, caller);
        survived.ShouldNotBeNull();
    }

    /// <summary>
    /// The counterweight: an entity that does not declare <c>softDelete</c> still deletes, so the refusal
    /// above cannot be implemented as "refuse every delete".
    /// </summary>
    [Fact]
    public async Task A_delete_on_an_ordinary_entity_still_removes_the_row()
    {
        var fixture = await NotesFixtureAsync();

        await fixture.Data.DeleteAsync("notes", fixture.AliceRow1Id, fixture.Alice);

        (await fixture.Data.GetAsync("notes", fixture.AliceRow1Id, fixture.Alice)).ShouldBeNull();
    }

    /// <summary>
    /// The four malformed-filter inputs on which the two shipped implementations of this port used to give
    /// <b>four different answers</b>, so PR3 could not map 422 from 403 from 500 by exception type. Settled:
    /// a malformed query is the <see cref="ArgumentException"/> family, a denial is
    /// <see cref="AlvoAuthorizationException"/>, and an internal invariant violation stays
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, <c>is</c> with a non-bool and <c>in</c> with a scalar were <c>AlvoAuthorizationException</c>
    /// on a real engine — so <c>status=is.hello</c>, an ordinary agent typo, read as "not authorized" — and
    /// excluded-but-answered in the reference; an unconvertible value and a fractional bound against an
    /// integral field were <c>InvalidOperationException</c>, the same type the binder uses for genuine internal
    /// invariant violations, and answered in the reference.
    /// </para>
    /// <para>
    /// It is one fact over all four because the point is that they share a channel. The counterweight is the
    /// following fact: a well-formed filter over the same fields still answers, so this cannot be satisfied by
    /// refusing every filter.
    /// </para>
    /// </remarks>
    /// <param name="malformed">A filter whose shape or value this port cannot serve.</param>
    [Theory]
    [MemberData(nameof(MalformedFilters))]
    public async Task A_malformed_filter_is_refused_on_the_malformed_query_channel(AlvoFilter malformed)
    {
        var fixture = await NotesFixtureAsync();

        await Should.ThrowAsync<ArgumentException>(() => fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Filter = malformed }, fixture.Alice));
    }

    /// <summary>
    /// The four inputs, each named for the shape it is: an <c>is</c> operand SQL's own <c>IS</c> does not
    /// accept, an <c>in</c> operand that is not a list, a value the field's type cannot hold, and a fractional
    /// bound against a field whose type has no fraction.
    /// </summary>
    public static TheoryData<AlvoFilter> MalformedFilters() =>
    [
        new AlvoComparison("title", AlvoFilterOperator.Is, "maybe"),
        new AlvoComparison("title", AlvoFilterOperator.In, "not-a-list"),
        new AlvoComparison("owner_id", AlvoFilterOperator.Eq, "not-a-uuid"),
        new AlvoComparison("owner_id", AlvoFilterOperator.In, Guid.NewGuid()),
    ];

    /// <summary>
    /// The counterweight to the refusals above: the same fields, well-formed, still answer — so the contract
    /// cannot be met by refusing everything.
    /// </summary>
    [Fact]
    public async Task A_well_formed_filter_over_the_same_fields_still_answers()
    {
        var fixture = await NotesFixtureAsync();

        var byTitle = (await fixture.Data.QueryAsync(
            new AlvoQuery
            {
                Entity = "notes",
                Filter = new AlvoComparison("title", AlvoFilterOperator.Is, null),
            },
            fixture.Alice)).Items;
        byTitle.ShouldBeEmpty();

        var byOwner = (await fixture.Data.QueryAsync(
            new AlvoQuery
            {
                Entity = "notes",
                Filter = new AlvoComparison(
                    "owner_id", AlvoFilterOperator.In, new object?[] { fixture.Alice.User.Value }),
            },
            fixture.Alice)).Items;
        byOwner.Count.ShouldBe(2);
    }

    /// <summary>
    /// The depth cap does not see <b>breadth</b>, and breadth is the same denial-of-service and the same
    /// engine divergence one level out: 900 <c>AND</c> terms answered on both engines and <b>1000 threw a
    /// raw <c>SqliteException</c></b> out of <see cref="IAlvoData.QueryAsync"/> while PostgreSQL answered,
    /// and 40 000 <c>in</c> candidates threw <c>too many SQL variables</c> after 3.5 s on SQLite where
    /// PostgreSQL answered in 0.27 s. Capped at the port so no backend can be the one that differs, on the
    /// same malformed-query channel as the depth cap.
    /// </summary>
    [Fact]
    public async Task A_filter_wider_than_the_term_cap_is_rejected_rather_than_composed()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);

        var atCap = (await fixture.Data.QueryAsync(QueryFilteredBy(WideFilter(AlvoFilter.MaxTerms)), member)).Items;
        atCap.ShouldNotBeNull();

        await Should.ThrowAsync<ArgumentException>(() => fixture.Data.QueryAsync(
            QueryFilteredBy(WideFilter(AlvoFilter.MaxTerms + 1)), member));
    }

    /// <summary>
    /// The <c>in</c> candidate cap, which is a limit on the <em>statement</em> rather than on the tree:
    /// every candidate becomes its own bind parameter.
    /// </summary>
    [Fact]
    public async Task An_in_list_longer_than_the_candidate_cap_is_rejected_rather_than_bound()
    {
        var fixture = await AccountsFixtureAsync();
        var member = NewContext(tenant: null);

        var atCap = (await fixture.Data.QueryAsync(QueryFilteredBy(InFilter(AlvoFilter.MaxInCandidates)), member)).Items;
        atCap.ShouldBeEmpty();

        await Should.ThrowAsync<ArgumentException>(() => fixture.Data.QueryAsync(
            QueryFilteredBy(InFilter(AlvoFilter.MaxInCandidates + 1)), member));
    }

    /// <summary>A conjunction of <paramref name="terms"/><c> - 1</c> comparisons, so the tree is exactly <paramref name="terms"/> nodes.</summary>
    /// <param name="terms">The total node count the tree must have.</param>
    private static AlvoAnd WideFilter(int terms) =>
        new([.. Enumerable.Range(0, terms - 1).Select(index => new AlvoComparison(
            "title", AlvoFilterOperator.Neq, $"absent-{index}"))]);

    private static AlvoComparison InFilter(int candidates) => new(
        "title",
        AlvoFilterOperator.In,
        Enumerable.Range(0, candidates).Select(index => $"absent-{index}").ToList());

    /// <summary>
    /// A keyset cursor's boundary used to be a chain of comparisons with no <c>IS NULL</c> arm, so a
    /// <see langword="null"/> on either side made the whole term <see langword="null"/> and a <c>WHERE</c>
    /// treated that as false — paging over a nullable sort key stopped at the first null-keyed row and
    /// <b>silently dropped the rest</b>. Measured under <c>nullslast</c>, three visible rows walked out as
    /// two; under <c>nullsfirst</c>, as one. F3 refused such a read rather than answer it wrongly; F4 answers
    /// it, because the boundary now compares the same <em>(rank, value)</em> pair the <c>ORDER BY</c> ranks by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This fact keeps the <b>adversarial</b> half of that history: a paged read over a nullable key answers,
    /// and walking it to exhaustion loses nothing. It is here rather than only in the paging suite because
    /// "the read is refused" and "the read silently drops rows" are the two failures an implementation can
    /// choose between when it cannot express the boundary, and both are visible from this suite's own fixture.
    /// The four-way <c>{asc,desc} × {nullsfirst,nullslast}</c> walk over a fixture that really contains nulls
    /// lives in <c>AlvoDataPagingTests</c>, which builds its own.
    /// </para>
    /// <para>
    /// An <b>unpaged</b> sorted read stays legal and is asserted alongside — without it, the fact could be
    /// satisfied by an implementation that had stopped sorting by a nullable key at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_paged_read_sorted_by_a_nullable_field_answers_and_loses_no_row()
    {
        var fixture = await NotesFixtureAsync();
        var sort = new[] { new AlvoSort("title") };

        var first = await fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1 }, fixture.Alice);
        var second = await fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1, After = first.NextCursor }, fixture.Alice);
        var unpaged = (await fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort }, fixture.Alice)).Items;

        unpaged.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();
        second.NextCursor.ShouldBeNull();
        first.Items.Concat(second.Items).Select(row => row["id"])
            .ShouldBe(unpaged.Select(row => row["id"]));
    }

    /// <summary>
    /// A cursor no page issued still finds no anchor and answers with an empty page — the property the
    /// nullable-key boundary must not have broken, since a null-keyed anchor is now a shape the renderer
    /// handles rather than one it refuses.
    /// </summary>
    [Fact]
    public async Task A_forged_cursor_over_a_nullable_sort_key_is_still_an_empty_page()
    {
        var fixture = await NotesFixtureAsync();

        var page = await fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 1, After = "any-cursor" },
            fixture.Alice);

        page.Items.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
    }

    /// <summary>
    /// The counterweight: paging by a <b>required</b> key is the supported shape and must keep working, so the
    /// refusal above cannot be satisfied by refusing every paged sorted read.
    /// </summary>
    [Fact]
    public async Task A_paged_read_sorted_by_a_required_field_still_answers()
    {
        var fixture = await NotesFixtureAsync();

        var page = (await fixture.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("owner_id")], Limit = 1 }, fixture.Alice)).Items;

        page.Count.ShouldBe(1);
    }

    private static AlvoQuery QueryFilteredBy(AlvoFilter filter) => new() { Entity = "accounts", Filter = filter };

    /// <summary>Builds a filter nesting <paramref name="depth"/> levels of <see cref="AlvoNot"/> over one comparison.</summary>
    /// <param name="depth">The number of nodes on the tree's single root-to-leaf path.</param>
    private static AlvoFilter NestedFilter(int depth)
    {
        AlvoFilter node = new AlvoComparison("title", AlvoFilterOperator.Eq, "Acct");
        for (var level = 1; level < depth; level++)
        {
            node = new AlvoNot(node);
        }

        return node;
    }

    /// <summary>
    /// A <b>global</b> entity whose rule references <c>@tenant.id</c> is not covered by the
    /// scoped-entity tenant guard, and the rule is <b>negated</b> — the shape that inverts an absent
    /// operand's collapse-to-false into "match every row". A tenantless caller must be refused
    /// outright, with no row set ever materialized, rather than served the whole table.
    /// </summary>
    [Fact]
    public async Task A_global_entity_whose_negated_rule_references_the_tenant_denies_a_tenantless_caller()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["region_id"] = new() { Type = DescField.Uuid, Required = true },
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "!(region_id == @tenant.id)", Get = "!(region_id == @tenant.id)" };
        var (descriptor, schema) = BuildFixture("ledgers", fields, EntityTenancy.Global, rules);
        var rowId = Guid.NewGuid();
        var seed = SeedOf("ledgers", Row(rowId, ("region_id", Guid.NewGuid()), ("title", "Ledger")));
        var data = await CreateAsync(schema, descriptor, seed);
        var tenantless = NewContext(tenant: null);

        IReadOnlyList<AlvoRecord>? captured = null;
        await Should.ThrowAsync<AlvoAuthorizationException>(async () =>
            captured = (await data.QueryAsync(new AlvoQuery { Entity = "ledgers" }, tenantless)).Items);

        captured.ShouldBeNull();
        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.GetAsync("ledgers", rowId, tenantless));
    }

    /// <summary>
    /// The identity half: <see cref="AlvoContext.Anonymous"/> carries the reserved all-zero user id,
    /// so an ownership rule would otherwise hand it every row whose owner column is all-zero — a row
    /// a partially-migrated or defaulted dataset really does contain. The seeded row is deliberately
    /// owned by exactly that all-zero uuid, so an implementation missing the gate returns it.
    /// </summary>
    [Fact]
    public async Task A_global_entity_whose_rule_references_the_user_denies_the_anonymous_caller()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = DescField.Uuid, Required = true },
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "owner_id == @user.id", Get = "owner_id == @user.id" };
        var (descriptor, schema) = BuildFixture("journals", fields, EntityTenancy.Global, rules);
        var rowId = Guid.NewGuid();
        var seed = SeedOf("journals", Row(rowId, ("owner_id", Guid.Empty), ("title", "Journal")));
        var data = await CreateAsync(schema, descriptor, seed);

        IReadOnlyList<AlvoRecord>? captured = null;
        await Should.ThrowAsync<AlvoAuthorizationException>(async () =>
            captured = (await data.QueryAsync(new AlvoQuery { Entity = "journals" }, AlvoContext.Anonymous)).Items);

        captured.ShouldBeNull();
        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.GetAsync("journals", rowId, AlvoContext.Anonymous));
    }

    /// <summary>
    /// The audit trail is the framework's to write, on every managed column and on both write paths.
    /// A caller that could supply <c>created_by</c> could create a row asserting a victim authored it —
    /// and on <c>create</c> there is no <c>USING</c> predicate to contradict the claim, only
    /// <c>WITH CHECK</c>, so a create rule that is anything other than a <c>created_by</c> comparison
    /// admits it. A caller that could supply <c>created_at</c>/<c>updated_at</c> could back-date the
    /// record. Both were live on both engines: the guard knew <c>id</c> and <c>tenant_id</c> while the
    /// schema mapper injected six columns.
    /// </summary>
    /// <remarks>
    /// The refusal is <see cref="AlvoAuthorizationException"/> like every other unwritable-field refusal,
    /// and it is decided from the payload alone, before any row is looked up — so it can never answer
    /// "does this row exist". The ordinary field is written in the same act, so the refusal cannot be
    /// satisfied by refusing the whole payload.
    /// </remarks>
    [Fact]
    public async Task A_payload_can_never_write_a_framework_managed_audit_column()
    {
        var fixture = await InvoicesFixtureAsync();
        var caller = fixture.Caller;

        foreach (var column in AlvoManagedColumns.Audit)
        {
            await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.CreateAsync(
                "invoices", Payload(("title", "forged"), (column, ForgedValue(column))), caller));
            await Should.ThrowAsync<AlvoAuthorizationException>(() => fixture.Data.UpdateAsync(
                "invoices", fixture.RowId, Payload((column, ForgedValue(column))), caller));
        }

        var updated = await fixture.Data.UpdateAsync("invoices", fixture.RowId, Payload(("title", "renamed")), caller);
        updated["title"].ShouldBe("renamed");
    }

    /// <summary>
    /// And the columns are actually populated, by the framework, from the caller's own identity and the
    /// implementation's clock. This is the half that makes the refusal above usable rather than merely
    /// safe: <c>created_at</c>/<c>updated_at</c> are <c>required</c>, so before this an audited create
    /// <b>failed</b> unless the caller supplied the very columns they may not write.
    /// </summary>
    [Fact]
    public async Task An_audited_create_stamps_the_caller_and_the_implementations_clock()
    {
        var fixture = await InvoicesFixtureAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var created = await fixture.Data.CreateAsync("invoices", Payload(("title", "new")), fixture.Caller);
        var after = DateTimeOffset.UtcNow.AddSeconds(5);

        created[AlvoManagedColumns.CreatedBy].ShouldBe(fixture.Caller.User.Value);
        created[AlvoManagedColumns.UpdatedBy].ShouldBe(fixture.Caller.User.Value);
        Stamp(created, AlvoManagedColumns.CreatedAt).ShouldBeInRange(before, after);
        Stamp(created, AlvoManagedColumns.UpdatedAt).ShouldBe(Stamp(created, AlvoManagedColumns.CreatedAt));

        var reread = await fixture.Data.GetAsync("invoices", (Guid)created["id"]!, fixture.Caller);
        reread.ShouldNotBeNull();
        reread![AlvoManagedColumns.CreatedBy].ShouldBe(fixture.Caller.User.Value);
    }

    /// <summary>
    /// An update stamps who wrote it and when, and leaves the creation record alone — an implementation
    /// that stamped all four on every write would erase the authorship the audit trail exists to record.
    /// </summary>
    [Fact]
    public async Task An_audited_update_stamps_the_updater_and_never_rewrites_the_creator()
    {
        var fixture = await InvoicesFixtureAsync();
        var created = await fixture.Data.CreateAsync("invoices", Payload(("title", "new")), fixture.Caller);
        var second = NewContext(tenant: null);

        var updated = await fixture.Data.UpdateAsync(
            "invoices", (Guid)created["id"]!, Payload(("title", "renamed")), second);

        updated[AlvoManagedColumns.UpdatedBy].ShouldBe(second.User.Value);
        updated[AlvoManagedColumns.CreatedBy].ShouldBe(fixture.Caller.User.Value);
        Stamp(updated, AlvoManagedColumns.CreatedAt).ShouldBe(Stamp(created, AlvoManagedColumns.CreatedAt));
        Stamp(updated, AlvoManagedColumns.UpdatedAt).ShouldBeGreaterThanOrEqualTo(Stamp(created, AlvoManagedColumns.UpdatedAt));
    }

    private static DateTimeOffset Stamp(AlvoRecord row, string column) =>
        ((DateTimeOffset)row[column]!).ToUniversalTime();

    /// <summary>A value of the column's own type, so the refusal cannot be a type failure in disguise.</summary>
    private static object ForgedValue(string column) => column == AlvoManagedColumns.CreatedBy
        || column == AlvoManagedColumns.UpdatedBy
            ? Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")
            : new DateTimeOffset(1989, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] fields) =>
        fields.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>
    /// Reserved parity leg: analysis §2.1 requires this whole suite to pass identically over a dynamic
    /// (metadata-driven) entity, and PR2's obligation was only to leave the mechanism capable of it —
    /// which it does by making the storage shape an <c>IAlvoSqlDialect</c> + <c>IFieldSqlRenderer</c>
    /// pair rather than a branch in the data path. Enabling this member is F7's, and it is declared
    /// here so the obligation is a named test rather than a paragraph in a design note.
    /// </summary>
    [Fact(Skip = "Dynamic driver lands in F7 — parity leg reserved (analysis §2.1).")]
    public Task Same_suite_passes_over_a_dynamic_entity() => Task.CompletedTask;

    /// <summary>
    /// Reserved F7 leg for one trap the de-risking spike (probe <c>X2</c>) already walked into, so it is a
    /// discovery already made rather than one waiting to happen. EF stores a <see cref="Guid"/> as
    /// <b>upper-case</b> <c>TEXT</c> on SQLite, while <c>json_extract</c> returns whatever case the stored
    /// payload holds — so a <c>uuid</c>-typed JSON path compares upper against lower and matches
    /// <b>nothing</b>, with no error from either side. Every row-ownership rule Alvo writes is a
    /// <c>uuid</c> comparison (<c>owner_id == @user.id</c>, <c>tenant_id == @tenant.id</c>), so on the
    /// dynamic driver this reads as an over-strict policy rather than as a bug. The dynamic driver must
    /// normalise the case of a <c>uuid</c>-typed JSON path per engine; this member is what says so.
    /// </summary>
    [Fact(Skip = "Dynamic driver lands in F7 — uuid JSON-path normalisation reserved (spike X2).")]
    public Task A_uuid_rule_over_a_dynamic_entity_matches_rows_on_every_engine() => Task.CompletedTask;

    private sealed record NotesFixture(IAlvoData Data, AlvoContext Alice, AlvoContext Bob, TenantId Tenant, Guid AliceRow1Id, Guid AliceRow2Id, Guid BobRowId);

    private sealed record DocumentsFixture(
        IAlvoData Data, TenantId Acme, TenantId Globex, TenantId Third, Guid AcmeRowId, Guid GlobexRowId, Guid ThirdRowId);

    private sealed record AccountsFixture(IAlvoData Data, Guid RowId, Guid SecondRowId);

    private sealed record InvoicesFixture(IAlvoData Data, AlvoContext Caller, Guid RowId);

    /// <summary>
    /// A global entity declaring <c>audit</c>, so the framework injects and owns the audit quartet. The
    /// seeded row carries its own audit values, because seeding bypasses policy by design and the two
    /// timestamp columns are <c>required</c>.
    /// </summary>
    private async Task<InvoicesFixture> InvoicesFixtureAsync()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };
        var (descriptor, schema) = BuildFixture("invoices", fields, EntityTenancy.Global, rules, audit: true);

        var seeded = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rowId = Guid.NewGuid();
        var seed = SeedOf(
            "invoices",
            Row(
                rowId,
                ("title", "Seeded"),
                (AlvoManagedColumns.CreatedAt, seeded),
                (AlvoManagedColumns.UpdatedAt, seeded)));

        var data = await CreateAsync(schema, descriptor, seed);
        return new InvoicesFixture(data, NewContext(tenant: null), rowId);
    }

    private async Task<NotesFixture> NotesFixtureAsync()
    {
        var tenant = TenantId.New();
        var alice = NewContext(tenant);
        var bob = NewContext(tenant);

        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = DescField.Uuid, Required = true },
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules
        {
            List = "owner_id == @user.id",
            Get = "owner_id == @user.id",
            Create = "owner_id == @user.id",
            Update = "owner_id == @user.id",
            Delete = "owner_id == @user.id",
        };
        var (descriptor, schema) = BuildFixture("notes", fields, EntityTenancy.Scoped, rules);

        var aliceRow1 = Guid.NewGuid();
        var aliceRow2 = Guid.NewGuid();
        var bobRow = Guid.NewGuid();
        var seed = SeedOf(
            "notes",
            Row(aliceRow1, ("owner_id", alice.User.Value), ("tenant_id", tenant.Value), ("title", "Alice-1")),
            Row(aliceRow2, ("owner_id", alice.User.Value), ("tenant_id", tenant.Value), ("title", "Alice-2")),
            Row(bobRow, ("owner_id", bob.User.Value), ("tenant_id", tenant.Value), ("title", "Bob-1")));

        var data = await CreateAsync(schema, descriptor, seed);
        return new NotesFixture(data, alice, bob, tenant, aliceRow1, aliceRow2, bobRow);
    }

    private async Task<DocumentsFixture> DocumentsFixtureAsync()
    {
        var acme = TenantId.New();
        var globex = TenantId.New();
        var third = TenantId.New();

        // title is required because A_query_limit_is_applied_after_the_policy_predicate_not_before pages by
        // it, and a keyset cursor cannot express where a nullable key's nulls sort — an implementation is
        // entitled to refuse a paged read over one rather than silently drop rows at the first null-keyed
        // row. Required-ness is incidental to every fact this fixture serves (every seeded row carries a
        // title, and every create supplies one), so declaring it costs the suite nothing.
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String, Required = true },
        };
        var rules = new AccessRules { List = "true", Get = "true", Create = "true" };
        var (descriptor, schema) = BuildFixture("documents", fields, EntityTenancy.Scoped, rules);

        var acmeRow = Guid.NewGuid();
        var globexRow = Guid.NewGuid();
        var thirdRow = Guid.NewGuid();
        var seed = SeedOf(
            "documents",
            Row(acmeRow, ("tenant_id", acme.Value), ("title", "Acme-doc")),
            Row(globexRow, ("tenant_id", globex.Value), ("title", "Globex-doc")),
            Row(thirdRow, ("tenant_id", third.Value), ("title", "Third-doc")));

        var data = await CreateAsync(schema, descriptor, seed);
        return new DocumentsFixture(data, acme, globex, third, acmeRow, globexRow, thirdRow);
    }

    private async Task<AccountsFixture> AccountsFixtureAsync()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
            ["secret"] = new() { Type = DescField.String, Hidden = BoolOrCel.FromBoolean(true) },
            ["note"] = new() { Type = DescField.String, Hidden = BoolOrCel.FromExpression("!('admin' in @user.roles)") },
            ["status"] = new() { Type = DescField.String, ReadOnly = BoolOrCel.FromBoolean(true) },
        };
        var rules = new AccessRules { List = "true", Get = "true", Update = "true" };
        var (descriptor, schema) = BuildFixture("accounts", fields, EntityTenancy.Global, rules);

        var rowId = Guid.NewGuid();
        var secondRowId = Guid.NewGuid();
        var seed = SeedOf(
            "accounts",
            Row(rowId, ("title", "Acct"), ("secret", "shh"), ("note", "internal"), ("status", "active")),
            Row(secondRowId, ("title", "Acct-2"), ("secret", "shh-2"), ("note", "internal-2"), ("status", "active")));

        var data = await CreateAsync(schema, descriptor, seed);
        return new AccountsFixture(data, rowId, secondRowId);
    }

    private static Dictionary<string, IReadOnlyList<AlvoRecord>> EmptySeed() =>
        new(StringComparer.Ordinal);

    private static Dictionary<string, IReadOnlyList<AlvoRecord>> SeedOf(string entity, params AlvoRecord[] rows) =>
        new(StringComparer.Ordinal) { [entity] = rows };

    private static AlvoRecord Row(Guid id, params (string Field, object? Value)[] fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id };
        foreach (var (field, value) in fields)
        {
            values[field] = value;
        }

        return new AlvoRecord(values);
    }

    private static AlvoContext NewContext(TenantId? tenant, params Role[] extraRoles)
    {
        var roles = new HashSet<Role> { Role.Authenticated };
        foreach (var role in extraRoles)
        {
            roles.Add(role);
        }

        return new AlvoContext { User = UserId.New(), Roles = roles, Tenant = tenant };
    }

    /// <summary>
    /// Builds a matching (descriptor, schema) pair for one entity, by hand mirroring the managed-column
    /// injection <c>DescriptorToSchemaMapper</c> performs in the core — that mapper is
    /// <see langword="internal"/>, unreachable from this project, so this local mirror keeps the two
    /// in sync manually. <em>Which</em> columns are managed is not mirrored: that comes from
    /// <see cref="AlvoManagedColumns"/>, the same authority the mapper and every driver's write guard read,
    /// so only each column's shape is restated here.
    /// </summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="fields">The entity's declared fields.</param>
    /// <param name="tenancy">The entity's tenancy.</param>
    /// <param name="rules">The entity's access rules, or <see langword="null"/> for none.</param>
    /// <param name="audit">Whether the entity declares <c>audit</c>, and therefore carries the audit quartet.</param>
    /// <param name="softDelete">Whether the entity declares <c>softDelete</c>, and therefore carries <c>deleted_at</c>.</param>
    private static (AlvoDescriptor Descriptor, SchemaModel Schema) BuildFixture(
        string entity,
        Dictionary<string, FieldDescriptor> fields,
        EntityTenancy tenancy,
        AccessRules? rules,
        bool audit = false,
        bool softDelete = false)
    {
        var entityDescriptor = new EntityDescriptor
        {
            Fields = fields,
            Tenancy = tenancy,
            Rules = rules,
            Audit = audit,
            SoftDelete = softDelete,
        };
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "adversarial-suite",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal) { [entity] = entityDescriptor },
        };

        var tenancyMode = tenancy == EntityTenancy.Scoped ? TenancyMode.Scoped : TenancyMode.Global;
        var schemaFields = new List<FieldSchema>();
        foreach (var (name, field) in fields)
        {
            schemaFields.Add(ToFieldSchema(name, field));
        }

        var managedColumns = AlvoManagedColumns.For(tenancyMode, audit, softDelete);
        foreach (var managed in managedColumns.Where(column => !fields.ContainsKey(column)))
        {
            schemaFields.Add(ManagedFieldSchema(managed));
        }

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = entity,
                Tenancy = tenancyMode,
                Audit = audit,
                SoftDelete = softDelete,
                Fields = schemaFields,
            },
        ]);
        return (descriptor, schema);
    }

    /// <summary>One managed column's shape, as the core's own mapper declares it.</summary>
    private static FieldSchema ManagedFieldSchema(string column)
    {
        if (column == AlvoManagedColumns.Id)
        {
            return new() { Name = column, Type = SchemaField.Uuid, Required = true };
        }

        if (column == AlvoManagedColumns.TenantId)
        {
            return new() { Name = column, Type = SchemaField.Uuid, Required = true, Indexed = true };
        }

        if (column == AlvoManagedColumns.CreatedAt || column == AlvoManagedColumns.UpdatedAt)
        {
            return new() { Name = column, Type = SchemaField.DateTime, Required = true };
        }

        if (column == AlvoManagedColumns.DeletedAt)
        {
            return new() { Name = column, Type = SchemaField.DateTime, Nullable = true };
        }

        return column == AlvoManagedColumns.CreatedBy || column == AlvoManagedColumns.UpdatedBy
            ? new FieldSchema { Name = column, Type = SchemaField.Uuid, Nullable = true }
            : throw new ArgumentOutOfRangeException(nameof(column), column, "Unmirrored managed column.");
    }

    private static FieldSchema ToFieldSchema(string name, FieldDescriptor field) => new()
    {
        Name = name,
        Type = ToSchemaFieldType(field.Type),
        Required = field.Required == true,
        Nullable = field.Nullable ?? field.Required != true,
    };

    private static SchemaField ToSchemaFieldType(DescField type) => type switch
    {
        DescField.String => SchemaField.String,
        DescField.Text => SchemaField.Text,
        DescField.Integer => SchemaField.Integer,
        DescField.Decimal => SchemaField.Decimal,
        DescField.Boolean => SchemaField.Boolean,
        DescField.Date => SchemaField.Date,
        DescField.DateTime => SchemaField.DateTime,
        DescField.Uuid => SchemaField.Uuid,
        DescField.Json => SchemaField.Json,
        DescField.Enum => SchemaField.Enum,
        DescField.Ref => SchemaField.Ref,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped field type."),
    };
}
