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
/// Sixteen facts come from the F3 design brief; two more (<see cref="A_nullable_boolean_field_used_bare_in_a_rule_works_end_to_end"/>
/// and <see cref="A_hidden_expression_that_cannot_resolve_still_masks_the_field"/>) were added
/// because earlier tasks found the exact bugs they guard against — a boolean row field used bare
/// in a rule, and a <c>hidden</c> expression that cannot resolve for the caller — so this suite
/// proves both fixes hold through the full port, not only at the CEL-interpreter unit-test level.
/// Every fact builds its own descriptor, schema, and seed rows with freshly generated ids — no
/// fact relies on another's data, on insertion order, or on a fixed literal id, so a real-database
/// subclass (PR2) can run every fact in any order, in parallel, against a store it does not reset
/// between facts.
/// </remarks>
public abstract class AlvoDataAdversarialTests
{
    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>,
    /// seeded with <paramref name="seed"/>'s rows. A real provider creates its physical schema from
    /// <paramref name="descriptor"/>/<paramref name="schema"/> and inserts <paramref name="seed"/>
    /// through its own write path before returning.
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

        var result = await fixture.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, fixture.Alice);

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

        var listed = await data.QueryAsync(new AlvoQuery { Entity = "logs" }, caller);
        listed.Count.ShouldBe(1);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => data.DeleteAsync("logs", Guid.NewGuid(), caller));
    }

    /// <summary>Acme's caller cannot see Globex's rows even with a permissive <c>"true"</c> rule.</summary>
    [Fact]
    public async Task A_tenant_scoped_entity_never_returns_another_tenants_rows()
    {
        var fixture = await DocumentsFixtureAsync();
        var acmeUser = NewContext(fixture.Acme);

        var result = await fixture.Data.QueryAsync(new AlvoQuery { Entity = "documents" }, acmeUser);

        result.Count.ShouldBe(1);
        result[0]["tenant_id"].ShouldBe(fixture.Acme.Value);
    }

    /// <summary>The §4 acceptance criterion: a throw, and no rows returned.</summary>
    [Fact]
    public async Task A_query_with_no_tenant_context_fails_rather_than_returning_every_tenants_rows()
    {
        var fixture = await DocumentsFixtureAsync();
        var tenantless = NewContext(tenant: null);

        IReadOnlyList<AlvoRecord>? captured = null;
        await Should.ThrowAsync<AlvoAuthorizationException>(async () =>
            captured = await fixture.Data.QueryAsync(new AlvoQuery { Entity = "documents" }, tenantless));

        captured.ShouldBeNull();
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

    /// <summary>Both a statically <c>hidden: true</c> field and a context-conditional one never appear in a returned record.</summary>
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

    /// <summary>The regression that justifies <c>@user.roles</c>: a caller holding {authenticated, admin} satisfies both rules.</summary>
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

        var listed = await data.QueryAsync(new AlvoQuery { Entity = "settings" }, multiRole);
        listed.Count.ShouldBe(1);

        var got = await data.GetAsync("settings", rowId, multiRole);
        got.ShouldNotBeNull();
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

        var result = await fixture.Data.QueryAsync(query, fixture.Alice);

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

        var visible = await data.QueryAsync(new AlvoQuery { Entity = "posts" }, alice);

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

    private sealed record NotesFixture(IAlvoData Data, AlvoContext Alice, AlvoContext Bob, TenantId Tenant, Guid AliceRow1Id, Guid AliceRow2Id, Guid BobRowId);

    private sealed record DocumentsFixture(IAlvoData Data, TenantId Acme, TenantId Globex, Guid AcmeRowId, Guid GlobexRowId);

    private sealed record AccountsFixture(IAlvoData Data, Guid RowId);

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

        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        var rules = new AccessRules { List = "true", Get = "true", Create = "true" };
        var (descriptor, schema) = BuildFixture("documents", fields, EntityTenancy.Scoped, rules);

        var acmeRow = Guid.NewGuid();
        var globexRow = Guid.NewGuid();
        var seed = SeedOf(
            "documents",
            Row(acmeRow, ("tenant_id", acme.Value), ("title", "Acme-doc")),
            Row(globexRow, ("tenant_id", globex.Value), ("title", "Globex-doc")));

        var data = await CreateAsync(schema, descriptor, seed);
        return new DocumentsFixture(data, acme, globex, acmeRow, globexRow);
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
        var seed = SeedOf("accounts", Row(rowId, ("title", "Acct"), ("secret", "shh"), ("note", "internal"), ("status", "active")));

        var data = await CreateAsync(schema, descriptor, seed);
        return new AccountsFixture(data, rowId);
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
    /// Builds a matching (descriptor, schema) pair for one entity, by hand mirroring the id/tenant_id
    /// column injection <c>DescriptorToSchemaMapper</c> performs in the core — that mapper is
    /// <see langword="internal"/>, unreachable from this project, so this local mirror keeps the two
    /// in sync manually. Limited to the F3 subset this suite needs (no audit/soft-delete columns).
    /// </summary>
    private static (AlvoDescriptor Descriptor, SchemaModel Schema) BuildFixture(
        string entity, Dictionary<string, FieldDescriptor> fields, EntityTenancy tenancy, AccessRules? rules)
    {
        var entityDescriptor = new EntityDescriptor { Fields = fields, Tenancy = tenancy, Rules = rules };
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "adversarial-suite",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal) { [entity] = entityDescriptor },
        };

        var schemaFields = new List<FieldSchema>();
        if (!fields.ContainsKey("id"))
        {
            schemaFields.Add(new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true });
        }

        foreach (var (name, field) in fields)
        {
            schemaFields.Add(ToFieldSchema(name, field));
        }

        var tenancyMode = tenancy == EntityTenancy.Scoped ? TenancyMode.Scoped : TenancyMode.Global;
        if (tenancyMode == TenancyMode.Scoped)
        {
            schemaFields.Add(new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true });
        }

        var schema = new SchemaModel([new EntitySchema { Name = entity, Tenancy = tenancyMode, Fields = schemaFields }]);
        return (descriptor, schema);
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
