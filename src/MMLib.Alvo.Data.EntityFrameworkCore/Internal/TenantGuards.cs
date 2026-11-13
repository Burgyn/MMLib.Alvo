using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>How <see cref="DescriptorModelBuilder"/> shapes the tenant guards of one model.</summary>
/// <param name="SetNull">How this engine keeps <c>onDelete: setNull</c> beside a guard.</param>
internal sealed record TenantGuardShape(ScopedSetNullShape SetNull)
{
    /// <summary>The guards of an engine that keeps <c>onDelete: setNull</c> as <paramref name="setNull"/> says.</summary>
    /// <param name="setNull">How this engine keeps <c>onDelete: setNull</c> beside a guard.</param>
    internal static TenantGuardShape For(ScopedSetNullShape setNull) => new(setNull);
}

/// <summary>
/// Recognises tenant guards: the foreign key <c>(child.tenant_id, child.ref) → (target.tenant_id, target.id)</c> a reference
/// between two scoped entities carries, over a <c>(tenant_id, id)</c> alternate key on the target (#161).
/// </summary>
internal static class TenantGuards
{
    /// <summary>Whether a reference from <paramref name="child"/> to <paramref name="target"/> is guarded: both are scoped.</summary>
    /// <param name="child">The entity declaring the reference.</param>
    /// <param name="target">The entity referenced.</param>
    internal static bool Applies(EntitySchema child, EntitySchema target) =>
        child.Tenancy == TenancyMode.Scoped && target.Tenancy == TenancyMode.Scoped;

    /// <summary>The <c>ref</c> fields of <paramref name="entity"/> that name a target.</summary>
    /// <param name="entity">The entity whose references are listed.</param>
    internal static IEnumerable<FieldSchema> References(EntitySchema entity) =>
        entity.Fields.Where(field => field.Type == FieldType.Ref && field.Reference is not null);

    /// <summary>The field a guard's columns protect — <c>x</c> of <c>[tenant_id, x]</c> — or <see langword="null"/>.</summary>
    /// <param name="columns">A foreign key's dependent columns.</param>
    internal static string? GuardedField(IReadOnlyList<string> columns) =>
        columns.Count == 2 && IsTenant(columns[0]) && !IsTenant(columns[1]) ? columns[1] : null;

    /// <summary>Whether <paramref name="op"/> adds a tenant guard: <c>[tenant_id, x] → [tenant_id, id]</c>.</summary>
    /// <param name="op">The operation inspected.</param>
    internal static bool IsGuard(AddForeignKeyOperation op) =>
        GuardedField(op.Columns) is not null && IsTenantAndId(op.PrincipalColumns);

    private static bool IsTenantAndId(IReadOnlyList<string>? columns) =>
        columns is [var tenant, var id] && IsTenant(tenant) && string.Equals(id, AlvoManagedColumns.Id, StringComparison.Ordinal);

    private static bool IsTenant(string column) => string.Equals(column, AlvoManagedColumns.TenantId, StringComparison.Ordinal);
}
