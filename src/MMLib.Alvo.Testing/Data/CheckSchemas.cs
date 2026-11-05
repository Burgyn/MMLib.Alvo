using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The CHECK constraint suite's products entity, shared by <see cref="AlvoDataCheckConstraintTests"/> and the one-engine
/// facts that stand the same entity up on their own (the PostgreSQL apply lock timeout).
/// </summary>
public static class CheckSchemas
{
    /// <summary>
    /// products: stock (validation "value >= 0"), discount (optional, "value &lt;= 50"), margin (hidden, "value >= 0"),
    /// sale_starts_at / sale_ends_at (sale_ends_at: "value - sale_starts_at &lt;= duration('720h')"), and the computed
    /// stock_value (stock + stock — a computed field carries no numeric constant, "value &lt;= 500"), and temperature
    /// ("value >= -40" — a negative constant, Ruling 16), and rating (hidden from admins only, "value &lt;= 5").
    /// </summary>
    public static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "check-constraints",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["products"] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["stock"] = new() { Type = DescField.Integer, Required = true, Validation = "value >= 0" },
                    ["discount"] = new() { Type = DescField.Integer, Validation = "value <= 50" },
                    ["margin"] = new() { Type = DescField.Decimal, Precision = 10, Scale = 2, Hidden = BoolOrCel.FromBoolean(true), Validation = "value >= 0" },
                    ["sale_starts_at"] = new() { Type = DescField.DateTime },
                    ["sale_ends_at"] = new() { Type = DescField.DateTime, Validation = "value - sale_starts_at <= duration('720h')" },
                    ["stock_value"] = new() { Type = DescField.Integer, Computed = "stock + stock", Validation = "value <= 500" },
                    ["temperature"] = new() { Type = DescField.Integer, Validation = "value >= -40" },
                    ["rating"] = new() { Type = DescField.Integer, Hidden = BoolOrCel.FromExpression("'admin' in @user.roles"), Validation = "value <= 5" },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };

    /// <summary>The same entity as a schema; each CheckExpression is the canonical form the mapper would write.</summary>
    public static SchemaModel Validated => new([
        new EntitySchema
        {
            Name = "products",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "stock", Type = SchemaField.Integer, Required = true, CheckExpression = "stock >= 0" },
                new FieldSchema { Name = "discount", Type = SchemaField.Integer, Nullable = true, CheckExpression = "discount <= 50" },
                new FieldSchema { Name = "margin", Type = SchemaField.Decimal, Nullable = true, Precision = 10, Scale = 2, CheckExpression = "margin >= 0" },
                new FieldSchema { Name = "sale_starts_at", Type = SchemaField.DateTime, Nullable = true },
                new FieldSchema
                {
                    Name = "sale_ends_at", Type = SchemaField.DateTime, Nullable = true,
                    CheckExpression = "sale_ends_at - sale_starts_at <= duration('2592000s')",
                },
                new FieldSchema
                {
                    Name = "stock_value", Type = SchemaField.Integer, Nullable = true,
                    ComputedExpression = "stock + stock", CheckExpression = "stock_value <= 500",
                },
                new FieldSchema { Name = "temperature", Type = SchemaField.Integer, Nullable = true, CheckExpression = "temperature >= -40" },
                new FieldSchema { Name = "rating", Type = SchemaField.Integer, Nullable = true, CheckExpression = "rating <= 5" },
            ],
        },
    ]);

    /// <summary><see cref="Validated"/> with no field declaring a validation — the table before a validation is added.</summary>
    public static SchemaModel Unvalidated => new([
        Validated.Entities[0] with
        {
            Fields = [.. Validated.Entities[0].Fields.Select(declared => declared with { CheckExpression = null })],
        },
    ]);
}
