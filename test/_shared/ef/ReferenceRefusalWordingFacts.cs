using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// Which wording the shipped drivers give a refused reference (#161), on whichever engine the deriving class supplies: a
/// create or update whose <c>ref</c> names a row it cannot point at is worded for the write ("point the reference
/// elsewhere"), and a delete a <c>restrict</c> reference refuses keeps the delete's ("remove what points here").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here and not in the published constraint suite.</b> The write's wording is built by an <see langword="internal"/>
/// factory, so a third-party provider inheriting <c>AlvoDataConstraintTests</c> could never produce it; that suite asserts
/// only what the public constructors can satisfy — the kind, and that a cross-tenant refusal equals a no-such-row one.
/// What the drivers choose is pinned here, against the engine, because the choice is made per write call site
/// (<c>removes</c>) and only a real foreign-key failure reaches it.
/// </para>
/// </remarks>
public abstract class ReferenceRefusalWordingFacts
{
    private const string WorkOrders = "work_orders";

    private const string WorkOrderId = "work_order_id";

    private static readonly AlvoContext _north = Caller();

    private static readonly AlvoContext _south = Caller();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Builds a fresh <see cref="IAlvoData"/> over <paramref name="schema"/> and <paramref name="descriptor"/>.</summary>
    /// <param name="schema">The schema to migrate.</param>
    /// <param name="descriptor">The descriptor whose rules and tenancy apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>A create naming another tenant's row, or no row, is worded for the write.</summary>
    /// <param name="child">The referencing entity — one per <c>onDelete</c>.</param>
    [Theory]
    [InlineData("visits")]
    [InlineData("tasks")]
    [InlineData("notes")]
    public async Task A_create_naming_a_row_it_cannot_reference_is_worded_for_the_write(string child)
    {
        var data = await CreateAsync(TenantGuardSchemas.AllActions(), Descriptor);
        var theirs = await CreateWorkOrderAsync(data, _south);

        ShouldBeWordedForTheWrite(await Should.ThrowAsync<AlvoConstraintViolationException>(() => CreateChildAsync(data, child, theirs)));
        ShouldBeWordedForTheWrite(await Should.ThrowAsync<AlvoConstraintViolationException>(() => CreateChildAsync(data, child, Guid.NewGuid())));
    }

    /// <summary>An update re-pointing a reference at another tenant's row, or at no row, is worded for the write.</summary>
    /// <param name="child">The referencing entity — one per <c>onDelete</c>.</param>
    [Theory]
    [InlineData("visits")]
    [InlineData("tasks")]
    [InlineData("notes")]
    public async Task An_update_naming_a_row_it_cannot_reference_is_worded_for_the_write(string child)
    {
        var data = await CreateAsync(TenantGuardSchemas.AllActions(), Descriptor);
        var row = (Guid)(await CreateChildAsync(data, child, await CreateWorkOrderAsync(data, _north)))["id"]!;
        var theirs = await CreateWorkOrderAsync(data, _south);

        ShouldBeWordedForTheWrite(await Should.ThrowAsync<AlvoConstraintViolationException>(() => RepointAsync(data, child, row, theirs)));
        ShouldBeWordedForTheWrite(await Should.ThrowAsync<AlvoConstraintViolationException>(() => RepointAsync(data, child, row, Guid.NewGuid())));
    }

    /// <summary>A delete a <c>restrict</c> reference refuses keeps the delete's wording, for one row and for a batch.</summary>
    [Fact]
    public async Task A_delete_a_restricting_reference_refuses_is_worded_for_the_delete()
    {
        var data = await CreateAsync(TenantGuardSchemas.AllActions(), Descriptor);
        var parent = await CreateWorkOrderAsync(data, _north);
        await CreateChildAsync(data, "visits", parent);

        ShouldBeWordedForTheDelete(await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.DeleteAsync(WorkOrders, parent, _north, cancellationToken: Ct)));
        ShouldBeWordedForTheDelete(await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.DeleteManyAsync(WorkOrders, [parent], _north, cancellationToken: Ct)));
    }

    private static void ShouldBeWordedForTheWrite(AlvoConstraintViolationException refusal)
    {
        refusal.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        refusal.IsUnresolvedReference.ShouldBeTrue("the write sent a reference and removed nothing");
        refusal.Message.ShouldBe(AlvoConstraintViolationException.UnresolvedReference(innerException: null).Message);
    }

    private static void ShouldBeWordedForTheDelete(AlvoConstraintViolationException refusal)
    {
        refusal.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        refusal.IsUnresolvedReference.ShouldBeFalse("a refused delete is answered with the delete's fix, not a write's");
        refusal.Message.ShouldBe(new AlvoConstraintViolationException(AlvoConstraintKind.Referenced, []).Message);
    }

    private static async Task<Guid> CreateWorkOrderAsync(IAlvoData data, AlvoContext caller) =>
        (Guid)(await data.CreateAsync(WorkOrders, Row(caller, ("title", "WO")), caller, cancellationToken: Ct))["id"]!;

    private static Task<AlvoRecord> CreateChildAsync(IAlvoData data, string child, Guid workOrderId) =>
        data.CreateAsync(child, Row(_north, (WorkOrderId, workOrderId)), _north, cancellationToken: Ct);

    private static Task<AlvoRecord> RepointAsync(IAlvoData data, string child, Guid row, Guid workOrderId) =>
        data.UpdateAsync(
            child, row, new Dictionary<string, object?>(StringComparer.Ordinal) { [WorkOrderId] = workOrderId }, _north, cancellationToken: Ct);

    private static Dictionary<string, object?> Row(AlvoContext caller, (string Name, object Value) field) =>
        new(StringComparer.Ordinal) { ["tenant_id"] = caller.Tenant!.Value.Value, [field.Name] = field.Value };

    private static AlvoContext Caller() =>
        new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated }, Tenant = TenantId.New() };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "reference-wording",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [WorkOrders] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String, Required = true },
                },
                Rules = AllowAll,
            },
            ["visits"] = Child(OnDeleteAction.Restrict, required: true),
            ["tasks"] = Child(OnDeleteAction.Cascade, required: true),
            ["notes"] = Child(OnDeleteAction.SetNull, required: false),
        },
    };

    private static EntityDescriptor Child(OnDeleteAction onDelete, bool required) => new()
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
}
