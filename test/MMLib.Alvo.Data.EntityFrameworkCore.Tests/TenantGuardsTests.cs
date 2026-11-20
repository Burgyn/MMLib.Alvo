using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class TenantGuardsTests
{
    [Theory]
    [InlineData(TenancyMode.Scoped, TenancyMode.Scoped, true)]
    [InlineData(TenancyMode.Scoped, TenancyMode.Global, false)]
    [InlineData(TenancyMode.Global, TenancyMode.Scoped, false)]
    [InlineData(null, TenancyMode.Scoped, false)]
    public void Applies_only_to_a_reference_scoped_on_both_ends(TenancyMode? child, TenancyMode? target, bool expected) =>
        TenantGuards.Applies(Entity("visits", child), Entity("work_orders", target)).ShouldBe(expected);

    [Theory]
    [InlineData(new[] { "tenant_id", "x" }, "x")]
    [InlineData(new[] { "x" }, null)]
    [InlineData(new[] { "x", "tenant_id" }, null)]
    [InlineData(new[] { "tenant_id", "x", "y" }, null)]
    public void GuardedField_is_the_non_tenant_column_of_a_guard_and_null_otherwise(string[] columns, string? expected) =>
        TenantGuards.GuardedField(columns).ShouldBe(expected);

    [Theory]
    [InlineData(new[] { "tenant_id", "work_order_id" }, new[] { "tenant_id", "id" }, true)]
    [InlineData(new[] { "work_order_id" }, new[] { "id" }, false)]
    [InlineData(new[] { "tenant_id", "work_order_id" }, new[] { "tenant_id", "code" }, false)]
    public void IsGuard_recognises_a_foreign_key_over_tenant_and_id(string[] columns, string[] principal, bool expected) =>
        TenantGuards.IsGuard(new AddForeignKeyOperation { Columns = columns, PrincipalColumns = principal }).ShouldBe(expected);

    private static EntitySchema Entity(string name, TenancyMode? tenancy) => new()
    {
        Name = name,
        Tenancy = tenancy,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };
}
