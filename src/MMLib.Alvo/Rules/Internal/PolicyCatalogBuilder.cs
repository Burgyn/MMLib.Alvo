using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Compiles a <see cref="PolicyCatalog"/> from a descriptor and its mapped schema: every rule,
/// the synthesized tenant scope, and every <c>hidden</c>/<c>readOnly</c> field flag, collecting
/// every problem found (rather than stopping at the first) so an agent sees every fix it needs in
/// one round trip.
/// </summary>
internal static class PolicyCatalogBuilder
{
    private const string TenantScopeSource = "tenant_id == @tenant.id";

    /// <summary>Attempts to compile a <see cref="PolicyCatalog"/> from a descriptor and its mapped schema.</summary>
    /// <param name="descriptor">The project descriptor whose <c>rules</c>/<c>hidden</c>/<c>readOnly</c> are compiled.</param>
    /// <param name="schema">The schema <paramref name="descriptor"/> maps to; the authoritative set of entities to compile.</param>
    /// <param name="compiler">The CEL compiler every rule and field flag is compiled through.</param>
    /// <param name="catalog">The built catalog, when every rule compiled; otherwise <see langword="null"/>.</param>
    /// <param name="errors">Every compilation problem found; empty on success.</param>
    /// <returns><see langword="true"/> when every rule, tenant scope, and field flag compiled.</returns>
    public static bool TryBuild(
        AlvoDescriptor descriptor,
        SchemaModel schema,
        ICelCompiler compiler,
        out PolicyCatalog? catalog,
        out IReadOnlyList<DescriptorValidationError> errors)
    {
        var errorList = new List<DescriptorValidationError>();
        var entities = new Dictionary<string, EntityPolicy>(StringComparer.Ordinal);

        foreach (var entitySchema in schema.Entities)
        {
            var entityDescriptor = descriptor.Entities.GetValueOrDefault(entitySchema.Name);
            entities[entitySchema.Name] = BuildEntity(entitySchema.Name, entityDescriptor, entitySchema, compiler, errorList);
        }

        if (errorList.Count > 0)
        {
            catalog = null;
            errors = errorList;
            return false;
        }

        catalog = new PolicyCatalog(entities);
        errors = [];
        return true;
    }

    private static EntityPolicy BuildEntity(
        string name, EntityDescriptor? descriptor, EntitySchema schema, ICelCompiler compiler, List<DescriptorValidationError> errors)
    {
        var operations = CompileRules(name, descriptor?.Rules, schema, compiler, errors);
        var tenantScope = SynthesizeTenantScope(name, schema, compiler, errors);
        var fields = descriptor?.Fields ?? new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal);
        var hidden = CompileFieldFlags(name, "hidden", fields, field => field.Hidden, schema, compiler, errors);
        var readOnly = CompileFieldFlags(name, "readOnly", fields, field => field.ReadOnly, schema, compiler, errors);
        return new EntityPolicy(schema.Tenancy, tenantScope, operations, hidden, readOnly);
    }

    /// <summary>
    /// Compiles the five nullable rule strings into the per-operation <c>USING</c>/<c>WITH CHECK</c>
    /// pairs, exactly Postgres's <c>CREATE POLICY</c> mapping: <c>update</c> compiles its source once
    /// and reuses the same <see cref="CompiledExpression"/> instance for both slots.
    /// </summary>
    private static Dictionary<DataOperation, OperationPolicy> CompileRules(
        string entityName, AccessRules? rules, EntitySchema schema, ICelCompiler compiler, List<DescriptorValidationError> errors)
    {
        var list = CompileOperationRule(entityName, "list", rules?.List, schema, compiler, errors);
        var get = CompileOperationRule(entityName, "get", rules?.Get, schema, compiler, errors);
        var delete = CompileOperationRule(entityName, "delete", rules?.Delete, schema, compiler, errors);
        var create = CompileOperationRule(entityName, "create", rules?.Create, schema, compiler, errors);
        var update = CompileOperationRule(entityName, "update", rules?.Update, schema, compiler, errors);

        return new Dictionary<DataOperation, OperationPolicy>
        {
            [DataOperation.List] = new(list, null),
            [DataOperation.Get] = new(get, null),
            [DataOperation.Delete] = new(delete, null),
            [DataOperation.Create] = new(null, create),
            [DataOperation.Update] = new(update, update),
        };
    }

    private static CompiledExpression? CompileOperationRule(
        string entityName, string operationName, string? source, EntitySchema schema, ICelCompiler compiler, List<DescriptorValidationError> errors)
    {
        if (source is null)
        {
            return null;
        }

        var result = compiler.Compile(source, CelProfile.Rule, schema);
        if (result.IsSuccess)
        {
            return result.Expression;
        }

        errors.AddRange(result.Errors.Select(error => Error($"/entities/{entityName}/rules/{operationName}", error)));
        return null;
    }

    /// <summary>
    /// Synthesizes the tenant scope for a tenant-scoped entity by compiling
    /// <c>tenant_id == @tenant.id</c> through <see cref="ICelCompiler"/> — never hand-built — so it is
    /// type-checked like any other rule and fails loudly (as a build error, naming the entity) if the
    /// entity has no <c>tenant_id</c> field. A global entity gets no tenant scope at all.
    /// </summary>
    private static CompiledExpression? SynthesizeTenantScope(
        string entityName, EntitySchema schema, ICelCompiler compiler, List<DescriptorValidationError> errors)
    {
        if (schema.Tenancy != TenancyMode.Scoped)
        {
            return null;
        }

        var result = compiler.Compile(TenantScopeSource, CelProfile.Rule, schema);
        if (result.IsSuccess)
        {
            return result.Expression;
        }

        errors.AddRange(result.Errors.Select(error => Error($"/entities/{entityName}/tenancy", error)));
        return null;
    }

    private static Dictionary<string, FieldMask> CompileFieldFlags(
        string entityName,
        string flagName,
        IReadOnlyDictionary<string, FieldDescriptor> fields,
        Func<FieldDescriptor, BoolOrCel?> selector,
        EntitySchema schema,
        ICelCompiler compiler,
        List<DescriptorValidationError> errors)
    {
        var result = new Dictionary<string, FieldMask>(StringComparer.Ordinal);
        foreach (var (fieldName, field) in fields)
        {
            var mask = CompileFieldFlag(entityName, fieldName, flagName, selector(field), schema, compiler, errors);
            if (mask is not null)
            {
                result[fieldName] = mask.Value;
            }
        }

        return result;
    }

    private static FieldMask? CompileFieldFlag(
        string entityName, string fieldName, string flagName, BoolOrCel? flag, EntitySchema schema, ICelCompiler compiler, List<DescriptorValidationError> errors)
    {
        if (flag is null)
        {
            return null;
        }

        if (!flag.IsExpression)
        {
            return flag.Boolean == true ? FieldMask.Always : null;
        }

        var path = $"/entities/{entityName}/fields/{fieldName}/{flagName}";
        var result = compiler.Compile(flag.Expression!, CelProfile.Rule, schema);
        if (!result.IsSuccess)
        {
            errors.AddRange(result.Errors.Select(error => Error(path, error)));
            return null;
        }

        if (!ReferencesRowField(result.Expression!.Root))
        {
            return FieldMask.FromExpression(result.Expression);
        }

        errors.Add(new DescriptorValidationError(
            path,
            $"A '{flagName}' expression must not reference row fields; it is evaluated once per request against the caller/tenant context only.",
            "Row-dependent masking would require post-processing the returned rows, which the 'no in-memory post-filter' invariant forbids. Use a static true/false, or defer the row-dependent check to a future post-processing feature.",
            DescriptorValidationSeverity.Error));
        return null;
    }

    /// <summary>Walks a compiled tree for any reference to a row field — the construct <c>hidden</c>/<c>readOnly</c> must never contain.</summary>
    private static bool ReferencesRowField(CelNode node) => node switch
    {
        CelFieldRef or CelHas => true,
        CelUnary unary => ReferencesRowField(unary.Operand),
        CelBinary binary => ReferencesRowField(binary.Left) || ReferencesRowField(binary.Right),
        CelConditional conditional =>
            ReferencesRowField(conditional.Condition) || ReferencesRowField(conditional.WhenTrue) || ReferencesRowField(conditional.WhenFalse),
        _ => false,
    };

    private static DescriptorValidationError Error(string path, CelCompilationError error) =>
        new(path, error.Message, error.FixSuggestion, DescriptorValidationSeverity.Error);
}
