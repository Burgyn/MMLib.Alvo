using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Which fields the record form offers as controls, and which it shows read-only under "Calculated".
/// </summary>
public class FormFieldsTests
{
    private const string Descriptor = """
        {
          "entities": {
            "service_orders": {
              "fields": {
                "external_ref": { "type": "string", "readOnly": true },
                "cost_price": { "type": "decimal", "readOnly": "!('manager' in @user.roles)" },
                "discount": { "type": "decimal", "readOnly": false },
                "internal_notes": { "type": "text", "hidden": true }
              }
            }
          }
        }
        """;

    private static readonly FieldLocks _locks = DescriptorLens.Locks(Descriptor, "service_orders");

    private static readonly FieldMasks _masks = DescriptorLens.Masks(Descriptor, "service_orders");

    [Fact]
    public void Read_only_true_locks_for_everyone_and_a_cel_lock_for_some()
    {
        _locks.Always.ShouldBe(["external_ref"]);
        _locks.Conditional.ShouldBe(["cost_price"]);
    }

    [Fact]
    public void Computed_rollup_and_read_only_fields_are_calculated_on_an_edit()
        => FormFields.Shown(Orders(), _masks, _locks, creating: false).Select(field => field.Name)
            .ShouldBe(["external_ref", "cost_price", "labour_total", "lines_count"]);

    [Fact]
    public void Everything_else_is_a_control_and_the_managed_columns_are_neither()
        => FormFields.Editable(Orders(), _locks, creating: false).Select(field => field.Name)
            .ShouldBe(["order_number", "discount", "internal_notes"]);

    [Fact]
    public void A_cel_lock_is_a_control_on_a_create_so_a_required_one_can_still_be_supplied()
    {
        FormFields.Editable(Orders(), _locks, creating: true).Select(field => field.Name)
            .ShouldBe(["order_number", "cost_price", "discount", "internal_notes"]);
        FormFields.Shown(Orders(), _masks, _locks, creating: true).ShouldBeEmpty("a new record has no values to show");
    }

    [Fact]
    public void A_field_hidden_from_everyone_is_never_shown_read_only()
    {
        var locked = new FieldLocks(
            new HashSet<string>(["internal_notes"], StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        FormFields.Shown(Orders(), _masks, locked, creating: false).Select(field => field.Name)
            .ShouldNotContain("internal_notes");
    }

    private static EntitySchema Orders() => new()
    {
        Name = "service_orders",
        Audit = true,
        Fields =
        [
            new() { Name = "id", Type = FieldType.Uuid },
            new() { Name = "order_number", Type = FieldType.String, Required = true },
            new() { Name = "external_ref", Type = FieldType.String },
            new() { Name = "cost_price", Type = FieldType.Decimal },
            new() { Name = "discount", Type = FieldType.Decimal },
            new() { Name = "internal_notes", Type = FieldType.Text },
            new() { Name = "labour_total", Type = FieldType.Decimal, ComputedExpression = "labour_hours * labour_rate" },
            new()
            {
                Name = "lines_count",
                Type = FieldType.Integer,
                Rollup = new RollupSchema { From = "order_lines", Op = RollupOperation.Count, Via = "order_id" },
            },
            new() { Name = "created_at", Type = FieldType.DateTime },
        ],
    };
}
