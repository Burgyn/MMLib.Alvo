using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// What a constraint the <b>database</b> enforces does, over a real engine — the two defects the field-service
/// e2e suite found (#137, #138) and the coverage boundary it could not close (#139).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is its own suite rather than facts added to <see cref="AlvoDataAdversarialTests"/>.</b> That
/// suite is inherited by the in-memory reference implementation too, which declares no indexes and has no
/// engine to refuse a write — so a fact placed there would either be vacuous for it or would demand it grow a
/// constraint engine of its own. Every fact here needs a real store, and both shipped relational drivers
/// inherit it unchanged.
/// </para>
/// <para>
/// <b>Why it is inherited rather than written per driver, which is the whole of #139.</b> The e2e suite that
/// found both defects runs PostgreSQL only, and constraint surfacing is exactly the behaviour that differs by
/// engine: PostgreSQL raises <c>PostgresException</c> with an SQLSTATE and a constraint name, SQLite raises
/// <c>SqliteException</c> with an extended result code and names the columns only in its message, and neither
/// names anything at all for a foreign key. §0 principle 3 requires the behaviour <em>above</em> that seam to
/// be identical, and the only way to know it is to ask both engines the same questions.
/// </para>
/// <para>
/// <b>The tenancy facts are #137's engine-level half, and they are the load-bearing ones.</b> A unit fact over
/// the model can say the index spans <c>(tenant_id, reference)</c>; only a real engine can say that two tenants
/// may then hold one value <em>and</em> that one tenant still may not. Both directions are asserted, because a
/// fix that dropped uniqueness altogether would satisfy the first alone.
/// </para>
/// </remarks>
public abstract class AlvoDataConstraintTests
{
    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/> —
    /// the same seam <see cref="AlvoDataAdversarialTests.CreateAsync"/> is, minus the seed: every fact here
    /// writes its own rows through the port, because what is being measured is what a <em>write</em> does.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules and tenancy apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// #137. Two tenants may hold one value on a <c>unique</c> field of a <c>tenancy: "scoped"</c> entity — the
    /// constraint spans <c>(tenant_id, reference)</c>, so tenant B's create is not answered by whether tenant A
    /// holds the value.
    /// </summary>
    /// <remarks>
    /// This is the fact that was red before the index moved. It is stated as "both are accepted" rather than as
    /// "the two answers are indistinguishable", because at the port there is nothing left to compare: an
    /// accepted create returns a row and raises nothing, so equality of the outcomes <em>is</em> the property.
    /// The HTTP-level indistinguishability is pinned separately, by
    /// <c>test/teapie-field-service/080-Tenancy/002</c>.
    /// </remarks>
    [Fact]
    public async Task One_unique_value_may_be_held_by_two_tenants_at_once()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var south = NewContext(TenantId.New());

        var northRow = await CreateWorkOrderAsync(data, north, "WO-1001");
        var southRow = await CreateWorkOrderAsync(data, south, "WO-1001");

        northRow["id"].ShouldNotBe(southRow["id"], "two tenants' rows are two rows, not one shared row");
        northRow["reference"].ShouldBe("WO-1001");
        southRow["reference"].ShouldBe("WO-1001");
    }

    /// <summary>
    /// #137's other half: the constraint must still <b>hold</b> inside a tenant. A fix that only widened the
    /// index would pass the fact above and silently allow two rows with one reference in one tenant, which is
    /// the whole thing <c>unique</c> was declared for.
    /// </summary>
    [Fact]
    public async Task A_duplicate_unique_value_inside_one_tenant_is_still_refused()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        await CreateWorkOrderAsync(data, north, "WO-1001");

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateWorkOrderAsync(data, north, "WO-1001"));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Unique);
        refusal.Fields.ShouldBe(["reference"], "the caller cannot repair a request that does not name the field");
    }

    /// <summary>
    /// #137's third direction: an entity with no tenancy keeps <b>instance-wide</b> uniqueness. Tenancy is what
    /// narrows the constraint, so where there is no tenant boundary there is nothing to narrow — and a fix that
    /// scoped every unique index would have quietly weakened this.
    /// </summary>
    [Fact]
    public async Task A_unique_field_on_a_non_scoped_entity_is_still_unique_across_the_instance()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var first = NewContext(TenantId.New());
        var second = NewContext(TenantId.New());
        await CreateCodeAsync(data, first, "SHARED");

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateCodeAsync(data, second, "SHARED"));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Unique);
        refusal.Fields.ShouldBe(["value"]);
    }

    /// <summary>#138. An update that collides is the same refusal as a create that does, naming the same field.</summary>
    /// <remarks>
    /// A separate fact because it is a separate statement: a create's failure comes out of <c>SaveChanges</c> and
    /// an update's out of <c>ExecuteUpdate</c>, which are different EF paths and — measured on SQLite — do not
    /// even report the same error detail. A driver that translated one and not the other would answer 409 for a
    /// duplicate on create and 500 for the identical duplicate on update.
    /// </remarks>
    [Fact]
    public async Task A_duplicate_unique_value_on_update_is_refused_naming_the_field()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        await CreateWorkOrderAsync(data, north, "WO-1001");
        var second = await CreateWorkOrderAsync(data, north, "WO-1002");

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.UpdateAsync(
            WorkOrders,
            (Guid)second["id"]!,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = "WO-1001" },
            north,
            cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Unique);
        refusal.Fields.ShouldBe(["reference"]);
    }

    /// <summary>
    /// #138's second shape: deleting a row another row still references through a <c>ref</c> declaring
    /// <c>onDelete: "restrict"</c> is a conflict, not a broken invariant — the descriptor <em>asked</em> the
    /// store for this refusal.
    /// </summary>
    /// <remarks>
    /// It names no field, on either engine and deliberately: which entity holds the referencing row is a fact
    /// about data the caller may have no read access to, and SQLite reports nothing beyond
    /// <c>FOREIGN KEY constraint failed</c> anyway. What the caller gets is the kind, which is enough to know
    /// the fix is "remove what points at this row" rather than "send a different value".
    /// </remarks>
    [Fact]
    public async Task Deleting_a_row_another_row_still_references_is_refused_as_a_conflict()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var parent = await CreateWorkOrderAsync(data, north, "WO-1001");
        await CreateLineItemAsync(data, north, (Guid)parent["id"]!);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.DeleteAsync(WorkOrders, (Guid)parent["id"]!, north, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        refusal.Fields.ShouldBeEmpty("naming the referencing entity would disclose data the caller may not read");
        (await data.GetAsync(WorkOrders, (Guid)parent["id"]!, north, Ct)).ShouldNotBeNull(
            "a refused delete must leave the row alone");
    }

    /// <summary>
    /// The counterweight to the fact above: a row nothing references still deletes. Without it, an
    /// implementation that refused every delete would satisfy the refusal and break the feature.
    /// </summary>
    [Fact]
    public async Task Deleting_a_row_nothing_references_still_succeeds()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var row = await CreateWorkOrderAsync(data, north, "WO-1001");

        await data.DeleteAsync(WorkOrders, (Guid)row["id"]!, north, cancellationToken: Ct);

        (await data.GetAsync(WorkOrders, (Guid)row["id"]!, north, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// The refusal discloses the field and nothing else: not the value the caller sent, not the engine's
    /// constraint or index name, not its message, not a stack frame.
    /// </summary>
    /// <remarks>
    /// The message reaches an HTTP caller as the problem document's <c>detail</c>, so anything in it is
    /// published. The engine's own text is not lost — it survives as the inner exception, which is where a
    /// host's logging reads it — and that is asserted here too, because a translation that dropped it would
    /// leave an operator with nothing to diagnose from.
    /// </remarks>
    [Fact]
    public async Task A_conflict_discloses_the_field_and_nothing_else()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        await CreateWorkOrderAsync(data, north, "WO-SECRET-1001");

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateWorkOrderAsync(data, north, "WO-SECRET-1001"));

        refusal.Message.ShouldNotContain("WO-SECRET-1001", Case.Sensitive);
        refusal.Message.ShouldNotContain("IX_", Case.Sensitive);
        refusal.Message.ShouldNotContain("constraint failed", Case.Insensitive);
        refusal.Message.ShouldNotContain("23505", Case.Sensitive);
        refusal.InnerException.ShouldNotBeNull("the engine's own diagnostics must survive for the host's log");
    }

    /// <summary>
    /// #161. A <c>ref</c> between two <c>tenancy: "scoped"</c> entities naming another tenant's row is refused exactly as
    /// one naming no row at all — under every <c>onDelete</c>, because the reference is a key over
    /// <c>(tenant_id, id)</c> and the caller's own <c>tenant_id</c> is half of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the port, not HTTP.</b> A generated route resolves a reference through the caller's own read before the
    /// port is reached (<c>unresolved-reference</c>); host code, an automation action or a hook's <c>mutate</c> writes
    /// through the port with no such check, so before the key a row of tenant A could name tenant B's row and be stored.
    /// </para>
    /// <para>
    /// <b>Indistinguishable is the property</b>: same type, kind, fields and message — anything that differed would be an
    /// existence oracle across tenants, one write per candidate id. And the wording is the write side's own: the caller
    /// sent a reference, so a refusal telling them to remove records that point at this one would send them the wrong way.
    /// </para>
    /// </remarks>
    /// <param name="child">The referencing entity — one per <c>onDelete</c> (restrict, cascade, setNull).</param>
    [Theory]
    [InlineData(Visits)]
    [InlineData(Tasks)]
    [InlineData(Notes)]
    public async Task A_reference_to_another_tenants_row_is_refused_exactly_as_one_to_no_row(string child)
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var south = NewContext(TenantId.New());
        var theirs = await CreateWorkOrderAsync(data, south, "WO-SOUTH");

        var crossTenant = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateChildAsync(data, north, child, (Guid)theirs["id"]!));
        var nowhere = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateChildAsync(data, north, child, Guid.NewGuid()));

        ShouldBeIndistinguishable(crossTenant, nowhere);
        (await ListChildrenAsync(data, north, child)).ShouldBeEmpty("a refused write stores nothing");
    }

    /// <summary>
    /// #161, the update path: re-pointing a stored reference at another tenant's row is refused exactly as re-pointing it
    /// at no row, and the reference keeps naming the caller's own row.
    /// </summary>
    /// <remarks>
    /// A separate fact for the reason <see cref="A_duplicate_unique_value_on_update_is_refused_naming_the_field"/> gives: a
    /// create fails out of <c>SaveChanges</c> and an update out of <c>ExecuteUpdate</c>, and a driver could translate one
    /// and not the other.
    /// </remarks>
    /// <param name="child">The referencing entity — one per <c>onDelete</c> (restrict, cascade, setNull).</param>
    [Theory]
    [InlineData(Visits)]
    [InlineData(Tasks)]
    [InlineData(Notes)]
    public async Task An_update_pointing_a_reference_at_another_tenants_row_is_refused_exactly_as_one_to_no_row(string child)
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var south = NewContext(TenantId.New());
        var ours = (Guid)(await CreateWorkOrderAsync(data, north, "WO-NORTH"))["id"]!;
        var theirs = (Guid)(await CreateWorkOrderAsync(data, south, "WO-SOUTH"))["id"]!;
        var row = (Guid)(await CreateChildAsync(data, north, child, ours))["id"]!;

        var crossTenant = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => RepointChildAsync(data, north, child, row, theirs));
        var nowhere = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => RepointChildAsync(data, north, child, row, Guid.NewGuid()));

        ShouldBeIndistinguishable(crossTenant, nowhere);
        (await data.GetAsync(child, row, north, Ct))![WorkOrderId].ShouldBe(ours, "a refused update leaves the row alone");
    }

    /// <summary>
    /// The counterweight to the two facts above: a reference to a row of the caller's <b>own</b> tenant is stored and reads
    /// back. Without it, a key that refused every reference would satisfy both refusals and break every <c>ref</c>.
    /// </summary>
    /// <param name="child">The referencing entity — one per <c>onDelete</c> (restrict, cascade, setNull).</param>
    [Theory]
    [InlineData(Visits)]
    [InlineData(Tasks)]
    [InlineData(Notes)]
    public async Task A_reference_to_a_row_of_the_callers_own_tenant_is_accepted(string child)
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var ours = (Guid)(await CreateWorkOrderAsync(data, north, "WO-NORTH"))["id"]!;

        var row = (Guid)(await CreateChildAsync(data, north, child, ours))["id"]!;

        (await data.GetAsync(child, row, north, Ct))![WorkOrderId].ShouldBe(ours);
    }

    /// <summary>
    /// #161: <c>onDelete: "restrict"</c> still refuses a delete when the reference is the <c>(tenant_id, id)</c> key —
    /// the restrict action rides on the tenant guard itself, so moving the key must not have dropped it.
    /// </summary>
    [Fact]
    public async Task Deleting_a_row_a_restricting_scoped_reference_names_is_refused()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var parent = (Guid)(await CreateWorkOrderAsync(data, north, "WO-NORTH"))["id"]!;
        await CreateChildAsync(data, north, Visits, parent);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.DeleteAsync(WorkOrders, parent, north, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        refusal.Fields.ShouldBeEmpty();
        (await data.GetAsync(WorkOrders, parent, north, Ct)).ShouldNotBeNull("a refused delete must leave the row alone");
    }

    /// <summary>#161: <c>onDelete: "cascade"</c> over the <c>(tenant_id, id)</c> key still removes the children.</summary>
    [Fact]
    public async Task Deleting_a_row_cascades_to_its_scoped_children()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var parent = (Guid)(await CreateWorkOrderAsync(data, north, "WO-NORTH"))["id"]!;
        var task = (Guid)(await CreateChildAsync(data, north, Tasks, parent))["id"]!;

        await data.DeleteAsync(WorkOrders, parent, north, cancellationToken: Ct);

        (await data.GetAsync(Tasks, task, north, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// #161: <c>onDelete: "setNull"</c> over the <c>(tenant_id, id)</c> key clears the reference and only the reference.
    /// </summary>
    /// <remarks>
    /// A composite <c>SET NULL</c> that nulled every column of the key would null the child's <c>tenant_id</c> too — a
    /// <c>NOT NULL</c> failure at best, and a row no tenant owns at worst. Reading the child back <b>as north</b> is what
    /// proves its tenant survived: a row whose <c>tenant_id</c> moved is invisible to its old tenant.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_row_clears_its_scoped_set_null_references_and_keeps_their_tenant()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var north = NewContext(TenantId.New());
        var parent = (Guid)(await CreateWorkOrderAsync(data, north, "WO-NORTH"))["id"]!;
        var note = (Guid)(await CreateChildAsync(data, north, Notes, parent))["id"]!;

        await data.DeleteAsync(WorkOrders, parent, north, cancellationToken: Ct);

        var stored = await data.GetAsync(Notes, note, north, Ct);
        stored.ShouldNotBeNull("the note keeps its tenant, so north still reads it");
        stored![WorkOrderId].ShouldBeNull();
    }

    private const string WorkOrders = "work_orders";

    private const string LineItems = "line_items";

    private const string Codes = "codes";

    private const string Visits = "visits";

    private const string Tasks = "tasks";

    private const string Notes = "notes";

    private const string WorkOrderId = "work_order_id";

    private static Task<AlvoRecord> CreateWorkOrderAsync(IAlvoData data, AlvoContext caller, string reference) =>
        data.CreateAsync(
            WorkOrders,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenant_id"] = caller.Tenant!.Value.Value,
                ["reference"] = reference,
                ["title"] = "A work order",
            },
            caller,
            cancellationToken: Ct);

    private static Task<AlvoRecord> CreateLineItemAsync(IAlvoData data, AlvoContext caller, Guid workOrderId) =>
        data.CreateAsync(
            LineItems,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["work_order_id"] = workOrderId,
                ["label"] = "A line",
            },
            caller,
            cancellationToken: Ct);

    private static Task<AlvoRecord> CreateCodeAsync(IAlvoData data, AlvoContext caller, string value) =>
        data.CreateAsync(
            Codes,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = value },
            caller,
            cancellationToken: Ct);

    private static Task<AlvoRecord> CreateChildAsync(IAlvoData data, AlvoContext caller, string child, Guid workOrderId) =>
        data.CreateAsync(
            child,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenant_id"] = caller.Tenant!.Value.Value,
                [WorkOrderId] = workOrderId,
            },
            caller,
            cancellationToken: Ct);

    private static Task<AlvoRecord> RepointChildAsync(IAlvoData data, AlvoContext caller, string child, Guid row, Guid workOrderId) =>
        data.UpdateAsync(
            child,
            row,
            new Dictionary<string, object?>(StringComparer.Ordinal) { [WorkOrderId] = workOrderId },
            caller,
            cancellationToken: Ct);

    private static async Task<IReadOnlyList<AlvoRecord>> ListChildrenAsync(IAlvoData data, AlvoContext caller, string child) =>
        (await data.QueryAsync(new AlvoQuery { Entity = child }, caller, Ct)).Items;

    /// <summary>
    /// Asserts a refusal of a reference to another tenant's row is the refusal of a reference to no row (#161), worded for
    /// the write that sent the reference rather than for a delete.
    /// </summary>
    private static void ShouldBeIndistinguishable(AlvoConstraintViolationException crossTenant, AlvoConstraintViolationException nowhere)
    {
        crossTenant.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        crossTenant.GetType().ShouldBe(nowhere.GetType());
        crossTenant.Kind.ShouldBe(nowhere.Kind);
        crossTenant.Fields.ShouldBe(nowhere.Fields);
        crossTenant.Message.ShouldBe(nowhere.Message);
        crossTenant.Message.ShouldNotBe(
            new AlvoConstraintViolationException(AlvoConstraintKind.Referenced, []).Message,
            "the write sent a reference and removed nothing, so the delete's wording would send the caller the wrong way");
    }

    private static AlvoContext NewContext(TenantId tenant) =>
        new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated }, Tenant = tenant };

    /// <summary>
    /// One entity per shape a constraint fact needs: a tenant-scoped one with a <c>unique</c> field, a
    /// non-scoped one with a <c>unique</c> field (so "scoping narrows it" and "nothing else narrows it" are two
    /// facts rather than one), a global one that references the first with <c>onDelete: "restrict"</c>, and three
    /// scoped ones that reference it under each <c>onDelete</c> — the <c>(tenant_id, id)</c> key of #161.
    /// </summary>
    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "constraint-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [WorkOrders] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["reference"] = new() { Type = DescField.String, Required = true, Unique = true },
                    ["title"] = new() { Type = DescField.String },
                },
                Rules = AllowAll,
            },
            [LineItems] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["work_order_id"] = new()
                    {
                        Type = DescField.Ref,
                        Entity = WorkOrders,
                        Required = true,
                        OnDelete = OnDeleteAction.Restrict,
                    },
                    ["label"] = new() { Type = DescField.String },
                },
                Rules = AllowAll,
            },
            [Codes] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["value"] = new() { Type = DescField.String, Required = true, Unique = true },
                },
                Rules = AllowAll,
            },
            [Visits] = ScopedChild(OnDeleteAction.Restrict, required: true),
            [Tasks] = ScopedChild(OnDeleteAction.Cascade, required: true),
            [Notes] = ScopedChild(OnDeleteAction.SetNull, required: false),
        },
    };

    /// <summary>A scoped entity whose one <c>ref</c> names <c>work_orders</c> — the #161 shape, one per <c>onDelete</c>.</summary>
    private static EntityDescriptor ScopedChild(OnDeleteAction onDelete, bool required) => new()
    {
        Tenancy = EntityTenancy.Scoped,
        Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            [WorkOrderId] = new() { Type = DescField.Ref, Entity = WorkOrders, Required = required, OnDelete = onDelete },
        },
        Rules = AllowAll,
    };

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason
    /// <c>AlvoDataAdversarialTests.BuildFixture</c> gives: the core's mapper is <see langword="internal"/> and
    /// unreachable from this project. <see cref="AlvoManagedColumns"/> stays the authority for <em>which</em>
    /// columns are managed; only each column's shape is restated.
    /// </summary>
    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = WorkOrders,
            Tenancy = TenancyMode.Scoped,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "reference", Type = SchemaField.String, Required = true, MaxLength = 40, Unique = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true },

                // Last, exactly as the core's mapper appends its managed columns — which is what made the
                // index emission unable to stay inside the per-field loop (#137).
                new FieldSchema { Name = AlvoManagedColumns.TenantId, Type = SchemaField.Uuid, Required = true, Indexed = true },
            ],
        },
        new EntitySchema
        {
            Name = LineItems,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema
                {
                    Name = "work_order_id",
                    Type = SchemaField.Ref,
                    Required = true,
                    Reference = new RefSchema(WorkOrders, MMLib.Alvo.Schema.OnDelete.Restrict),
                },
                new FieldSchema { Name = "label", Type = SchemaField.String, Nullable = true },
            ],
        },
        new EntitySchema
        {
            Name = Codes,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "value", Type = SchemaField.String, Required = true, MaxLength = 40, Unique = true },
            ],
        },
        ScopedChildSchema(Visits, MMLib.Alvo.Schema.OnDelete.Restrict, required: true),
        ScopedChildSchema(Tasks, MMLib.Alvo.Schema.OnDelete.Cascade, required: true),
        ScopedChildSchema(Notes, MMLib.Alvo.Schema.OnDelete.SetNull, required: false),
    ]);

    private static EntitySchema ScopedChildSchema(string name, MMLib.Alvo.Schema.OnDelete onDelete, bool required) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
            new FieldSchema
            {
                Name = WorkOrderId,
                Type = SchemaField.Ref,
                Required = required,
                Nullable = !required,
                Reference = new RefSchema(WorkOrders, onDelete),
            },
            new FieldSchema { Name = AlvoManagedColumns.TenantId, Type = SchemaField.Uuid, Required = true, Indexed = true },
        ],
    };
}
