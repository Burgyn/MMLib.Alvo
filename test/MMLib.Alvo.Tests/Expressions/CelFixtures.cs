using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The shared CEL compilation fixture for every expression test in this project: one entity
/// schema, one compiler, and a named cast of callers covering the combinations the security core
/// cares about — same tenant/different tenant, owner/non-owner, role-holding/not, and a caller
/// with no tenant at all. Kept here (rather than duplicated per test class) so later tasks (the
/// predicate renderer, the policy engine) compile against the exact same fixture the checker
/// itself was proven against.
/// </summary>
/// <remarks>
/// The named callers below are not redeclared here — they delegate to
/// <see cref="DifferentialRuleCases"/>, which cannot itself depend on this test project (a shared
/// library cannot reference a test project), so the one true definition lives there and the
/// differential test (Task 10) and this fixture agree on every caller by construction, never by
/// two definitions that happen to match.
/// </remarks>
internal static class CelFixtures
{
    internal static EntitySchema Orders { get; } = new()
    {
        Name = "orders",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["draft", "approved"] },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "payload", Type = FieldType.Json, Nullable = true },
            new FieldSchema { Name = "title", Type = FieldType.String, MaxLength = 200 },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "approved_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "is_public", Type = FieldType.Boolean, Nullable = true },
        ],
    };

    internal static ICelCompiler Compiler { get; } = new CelCompiler();

    /// <summary>The tenant every Acme-tenant context (<see cref="Alice"/>, <see cref="AcmeUser"/>, ...) acts in.</summary>
    internal static TenantId AcmeTenant => Alice.Tenant!.Value;

    /// <summary>An authenticated caller in the Acme tenant.</summary>
    internal static AlvoContext Alice => DifferentialRuleCases.Alice;

    /// <summary>A second, distinct authenticated caller in the Acme tenant — never <see cref="Alice"/>'s own row.</summary>
    internal static AlvoContext Bob => DifferentialRuleCases.Bob;

    /// <summary>An Acme-tenant caller holding the declared application role <c>editor</c>.</summary>
    internal static AlvoContext Editor => DifferentialRuleCases.Editor;

    /// <summary>
    /// An Acme-tenant caller holding <see cref="Role.Admin"/> plus <see cref="Role.Authenticated"/>
    /// — an admin is also an authenticated caller, so a rule like <c>'authenticated' in @user.roles</c>
    /// must pass for this context too, not only the built-in-admin-specific ones.
    /// </summary>
    internal static AlvoContext Admin => DifferentialRuleCases.Admin;

    /// <summary>A plain authenticated caller in the Acme tenant, for tenant-isolation tests.</summary>
    internal static AlvoContext AcmeUser => DifferentialRuleCases.AcmeUser;

    /// <summary>An authenticated caller in a different tenant than <see cref="AcmeUser"/>, for tenant-isolation tests.</summary>
    internal static AlvoContext OtherTenantUser => DifferentialRuleCases.OtherTenantUser;

    /// <summary><see cref="Alice"/> with no tenant — must be denied on a tenant-scoped entity, never widened to "all tenants".</summary>
    internal static AlvoContext TenantlessAlice => DifferentialRuleCases.TenantlessAlice;

    /// <summary>Compiles CEL source against <see cref="Orders"/> for the <see cref="CelProfile.Rule"/> profile.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <exception cref="InvalidOperationException">Compilation failed; the message joins every compiler error.</exception>
    internal static CompiledExpression CompileRule(string source) => Compile(source, CelProfile.Rule);

    /// <summary>Compiles CEL source against <see cref="Orders"/> for the <see cref="CelProfile.Condition"/> profile.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <exception cref="InvalidOperationException">Compilation failed; the message joins every compiler error.</exception>
    internal static CompiledExpression CompileCondition(string source) => Compile(source, CelProfile.Condition);

    /// <summary>Compiles CEL source against <see cref="Orders"/> for the <see cref="CelProfile.Computed"/> profile.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <exception cref="InvalidOperationException">Compilation failed; the message joins every compiler error.</exception>
    internal static CompiledExpression CompileComputed(string source) => Compile(source, CelProfile.Computed);

    /// <summary>Builds an <see cref="AlvoRecord"/> from field/value pairs for an interpreter test.</summary>
    /// <param name="fields">The record's field values.</param>
    internal static AlvoRecord Row(params (string Field, object? Value)[] fields) =>
        new(fields.ToDictionary(pair => pair.Field, pair => pair.Value));

    private static CompiledExpression Compile(string source, CelProfile profile)
    {
        var result = Compiler.Compile(source, profile, Orders);
        if (result.IsSuccess)
        {
            return result.Expression!;
        }

        var messages = string.Join("; ", result.Errors.Select(error => error.Message));
        throw new InvalidOperationException($"Failed to compile '{source}' as {profile}: {messages}");
    }
}
