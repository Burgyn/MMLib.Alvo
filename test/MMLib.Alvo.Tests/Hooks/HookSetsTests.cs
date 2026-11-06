using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>What a <c>Set</c> may write (Ruling 16): declared, not maintained by Alvo, a value its field holds, inside its facets.</summary>
public sealed class HookSetsTests
{
    private static readonly EntitySchema _entity = new()
    {
        Name = "bookings",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "title", Type = FieldType.String, MaxLength = 10 },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"] },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Precision = 6, Scale = 2 },
            new FieldSchema { Name = "net", Type = FieldType.Integer, Rollup = new RollupSchema { From = "lines", Via = "booking_id", Op = RollupOperation.Sum, Field = "amount" } },
            new FieldSchema { Name = "gross", Type = FieldType.Decimal, Precision = 8, Scale = 2, ComputedExpression = "total * 2" },
            new FieldSchema { Name = "deleted_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "created_by", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "updated_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "display", Type = FieldType.String, Nullable = true, IsVirtual = true },
        ],
        Audit = true,
        SoftDelete = true,
    };

    [Theory]
    [InlineData("nope", "x", "undeclared")]
    [InlineData("id", null, "maintained")]
    [InlineData("updated_at", null, "maintained")]
    [InlineData("net", 5L, "maintained")]
    [InlineData("gross", 5L, "maintained")]
    [InlineData("deleted_at", null, "maintained")]
    [InlineData("created_by", null, "maintained")]
    [InlineData("title", 42L, "type")]
    [InlineData("status", "superadmin", "enum-value")]
    [InlineData("title", "far too long a title", "max-length")]
    public void A_set_it_may_not_write_names_its_field_and_problem(string field, object? value, string problem) =>
        HookSets.FirstProblem(new Dictionary<string, object?> { [field] = value }, _entity).ShouldBe((field, problem));

    [Fact]
    public void A_hook_s_set_of_a_virtual_field_is_maintained() =>
        HookSets.FirstProblem(new Dictionary<string, object?> { ["display"] = "x" }, _entity).ShouldBe(("display", "maintained"));

    [Fact]
    public void A_whole_number_into_a_decimal_field_and_a_declared_value_are_accepted_and_widened()
    {
        var sets = new Dictionary<string, object?> { ["total"] = 12L, ["status"] = "closed", ["title"] = "ok" };

        HookSets.FirstProblem(sets, _entity).ShouldBeNull();
        HookSets.Coerced(sets, _entity)["total"].ShouldBe(12m);
    }

    [Theory]
    [InlineData(3)]
    [InlineData((short)3)]
    public void An_untyped_int_or_short_set_is_a_whole_number_its_field_holds(object value)
    {
        var run = HookContextTests.Run(MMLib.Alvo.Data.AlvoWriteOperation.Create, null, HookContextTests.Booking("Ada", 1));
        new BeforeWrite<MMLib.Alvo.Data.AlvoRecord>(run).Set("total", value);

        HookSets.FirstProblem(run.Sets, _entity).ShouldBeNull();
        HookSets.Coerced(run.Sets, _entity)["total"].ShouldBe(3m);
    }

    [Fact]
    public void A_fractional_double_into_a_decimal_field_stays_refused() =>
        HookSets.FirstProblem(new Dictionary<string, object?> { ["total"] = 1.5d }, _entity).ShouldBe(("total", "type"));

    [Fact]
    public void A_tenant_is_accepted_on_a_scoped_entity_and_refused_on_a_global_one()
    {
        var tenant = new Dictionary<string, object?> { ["tenant_id"] = Guid.NewGuid() };

        HookSets.FirstProblem(tenant, _entity with { Tenancy = TenancyMode.Scoped }).ShouldBeNull();
        HookSets.FirstProblem(tenant, _entity with { Tenancy = TenancyMode.Global }).ShouldBe(("tenant_id", "undeclared"));
    }
}
