using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Internal;
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
        var roles = RoleCatalog.FromDescriptor(descriptor);
        var entities = new Dictionary<string, EntityPolicy>(StringComparer.Ordinal);

        foreach (var entitySchema in schema.Entities)
        {
            var entityDescriptor = descriptor.Entities.GetValueOrDefault(entitySchema.Name);
            var build = new EntityBuild(entitySchema, compiler, roles, errorList);
            entities[entitySchema.Name] = BuildEntity(entityDescriptor, build);
        }

        if (errorList.Count > 0)
        {
            catalog = null;
            errors = errorList;
            return false;
        }

        catalog = new PolicyCatalog(entities, roles);
        errors = [];
        return true;
    }

    /// <summary>
    /// The inputs one entity's whole compilation needs, bundled so every helper below reads as "compile
    /// this source at this path" instead of re-threading four arguments that never vary within an entity.
    /// </summary>
    /// <param name="Schema">The entity every expression is type-checked against.</param>
    /// <param name="Compiler">The CEL compiler every expression goes through.</param>
    /// <param name="Roles">The project's declared roles, for validating role literals.</param>
    /// <param name="Errors">The shared accumulator every problem is appended to.</param>
    private sealed record EntityBuild(
        EntitySchema Schema, ICelCompiler Compiler, RoleCatalog Roles, List<DescriptorValidationError> Errors)
    {
        public string Name => Schema.Name;
    }

    private static EntityPolicy BuildEntity(EntityDescriptor? descriptor, EntityBuild build)
    {
        var tenantScope = SynthesizeTenantScope(build);
        var operations = CompileRules(descriptor?.Rules, tenantScope, build);
        var fields = descriptor?.Fields ?? new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal);
        var hidden = CompileFieldFlags("hidden", fields, field => field.Hidden, build);
        var readOnly = CompileFieldFlags("readOnly", fields, field => field.ReadOnly, build);
        return new EntityPolicy(build.Schema.Tenancy, tenantScope, operations, hidden, readOnly);
    }

    /// <summary>
    /// Compiles the five nullable rule strings into the per-operation <c>USING</c>/<c>WITH CHECK</c>
    /// pairs, exactly Postgres's <c>CREATE POLICY</c> mapping: <c>update</c> compiles its source once
    /// and reuses the same <see cref="CompiledExpression"/> instance for both slots.
    /// </summary>
    private static Dictionary<DataOperation, OperationPolicy> CompileRules(
        AccessRules? rules, CompiledExpression? tenantScope, EntityBuild build)
    {
        var list = CompileOperationRule(DataOperation.List, rules?.List, build);
        var get = CompileOperationRule(DataOperation.Get, rules?.Get, build);
        var delete = CompileOperationRule(DataOperation.Delete, rules?.Delete, build);
        var create = CompileOperationRule(DataOperation.Create, rules?.Create, build);
        var update = CompileOperationRule(DataOperation.Update, rules?.Update, build);

        return new Dictionary<DataOperation, OperationPolicy>
        {
            [DataOperation.List] = Operation(list, null, tenantScope),
            [DataOperation.Get] = Operation(get, null, tenantScope),
            [DataOperation.Delete] = Operation(delete, null, tenantScope),
            [DataOperation.Create] = Operation(null, create, tenantScope),
            [DataOperation.Update] = Operation(update, update, tenantScope),
        };
    }

    /// <summary>
    /// Assembles one <see cref="OperationPolicy"/>, precomputing here — once per apply, never per
    /// request — which caller/tenant context values the operation's predicates actually read.
    /// </summary>
    private static OperationPolicy Operation(
        CompiledExpression? @using, CompiledExpression? withCheck, CompiledExpression? tenantScope) => new(
            @using,
            withCheck,
            RequiresContextValue(CelContextValue.TenantId, @using, withCheck, tenantScope),
            RequiresContextValue(CelContextValue.UserId, @using, withCheck, tenantScope));

    private static bool RequiresContextValue(CelContextValue value, params CompiledExpression?[] predicates) =>
        predicates.Any(predicate => predicate is not null && ReferencesContextValue(predicate.Root, value));

    /// <summary>
    /// Walks a compiled tree for any reference to one caller/tenant context value. Deny-by-default in
    /// the same direction as <see cref="ReferencesRowField"/>: only the node kinds that provably
    /// cannot contain a context reference answer <see langword="false"/>, every composite recurses,
    /// and an unrecognized kind — a future construct this walk was never updated for — is treated as
    /// referencing the value, so the policy engine's gate errs towards denying rather than towards
    /// resolving a predicate against an absent operand.
    /// </summary>
    internal static bool ReferencesContextValue(CelNode node, CelContextValue value) => node switch
    {
        CelContextRef contextRef => contextRef.Value == value,
        CelLiteral or CelFieldRef or CelHas or CelChanged => false,
        CelUnary unary => ReferencesContextValue(unary.Operand, value),
        CelBinary binary => ReferencesContextValue(binary.Left, value) || ReferencesContextValue(binary.Right, value),
        CelConditional conditional =>
            ReferencesContextValue(conditional.Condition, value)
            || ReferencesContextValue(conditional.WhenTrue, value)
            || ReferencesContextValue(conditional.WhenFalse, value),
        _ => true,
    };

    private static CompiledExpression? CompileOperationRule(DataOperation operation, string? source, EntityBuild build) =>
        source is null ? null : CompileRuleExpression(source, $"/entities/{build.Name}/rules/{operation.ToWireName()}", build);

    /// <summary>
    /// Synthesizes the tenant scope for a tenant-scoped entity by compiling
    /// <c>tenant_id == @tenant.id</c> through <see cref="ICelCompiler"/> — never hand-built — so it is
    /// type-checked like any other rule and fails loudly (as a build error, naming the entity) if the
    /// entity has no <c>tenant_id</c> field. A global entity gets no tenant scope at all.
    /// </summary>
    private static CompiledExpression? SynthesizeTenantScope(EntityBuild build) =>
        build.Schema.Tenancy == TenancyMode.Scoped
            ? CompileRuleExpression(TenantScopeSource, $"/entities/{build.Name}/tenancy", build)
            : null;

    /// <summary>
    /// The one path from an authored Rule-profile source to a <see cref="CompiledExpression"/> this
    /// builder trusts: compile, then validate the role literals the compiler cannot judge. Both kinds of
    /// problem are reported on <paramref name="path"/> and both yield <see langword="null"/>, so a
    /// rejected expression never reaches a rule slot or a field mask.
    /// </summary>
    private static CompiledExpression? CompileRuleExpression(string source, string path, EntityBuild build)
    {
        var result = build.Compiler.Compile(source, CelProfile.Rule, build.Schema);
        if (!result.IsSuccess)
        {
            build.Errors.AddRange(result.Errors.Select(error => Error(path, error)));
            return null;
        }

        return HasDeclaredRoleLiterals(result.Expression!, path, build) ? result.Expression : null;
    }

    /// <summary>
    /// Validates every role literal in a compiled tree against the project's declared roles. A typo'd
    /// literal (<c>'amdin' in @user.roles</c>) compiles and type-checks perfectly and then simply never
    /// matches, so a rule written to admit admins admits nobody — or, negated, everybody.
    /// </summary>
    /// <remarks>
    /// Deliberately a post-compile walk here rather than a check inside <see cref="ICelCompiler.Compile"/>:
    /// the compiler judges one expression against one entity schema and has no role catalog. Declared
    /// roles are a project-level concern (the descriptor's <c>auth.roles</c> plus the built-ins), and the
    /// compiler is reachable from callers holding no descriptor at all.
    /// </remarks>
    private static bool HasDeclaredRoleLiterals(CompiledExpression expression, string path, EntityBuild build)
    {
        var undeclared = RoleLiterals(expression.Root)
            .Where(role => !build.Roles.TryGet(role, out _))
            .ToList();

        build.Errors.AddRange(undeclared.Select(role => UndeclaredRoleError(path, role, build.Roles)));
        return undeclared.Count == 0;
    }

    /// <summary>
    /// Every string literal tested for membership in <c>@user.roles</c>, walked iteratively. A
    /// membership test whose left operand is a row field names no role and yields nothing —
    /// <see cref="RoleMembership"/> already guarantees the right operand is the role set.
    /// </summary>
    private static IEnumerable<string> RoleLiterals(CelNode root)
    {
        var pending = new Stack<CelNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is CelBinary { Operator: CelBinaryOperator.In, Left: CelLiteral { Type: CelValueType.String, Value: string role } })
            {
                yield return role;
            }

            foreach (var child in CelTree.Children(node))
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Builds the "undeclared role" rejection, reusing the same "did you mean" shape an unknown field or
    /// enum value gets — a typo is by far the likeliest cause, so the fix names the nearest declared role.
    /// </summary>
    private static DescriptorValidationError UndeclaredRoleError(string path, string role, RoleCatalog roles)
    {
        var declared = roles.All.Select(candidate => candidate.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var closest = NameSuggestion.Closest(role, declared);
        var known = string.Join(", ", declared);

        return new(
            path,
            $"'{role}' is not a declared role, so this membership test can never match.",
            closest is not null
                ? $"Did you mean '{closest}'? Declared roles: {known}."
                : $"Declared roles: {known}. Add the role to auth.roles in the descriptor.",
            DescriptorValidationSeverity.Error);
    }

    private static Dictionary<string, FieldMask> CompileFieldFlags(
        string flagName,
        IReadOnlyDictionary<string, FieldDescriptor> fields,
        Func<FieldDescriptor, BoolOrCel?> selector,
        EntityBuild build)
    {
        var result = new Dictionary<string, FieldMask>(StringComparer.Ordinal);
        foreach (var (fieldName, field) in fields)
        {
            var mask = CompileFieldFlag(fieldName, flagName, selector(field), build);
            if (mask is not null)
            {
                result[fieldName] = mask.Value;
            }
        }

        return result;
    }

    private static FieldMask? CompileFieldFlag(string fieldName, string flagName, BoolOrCel? flag, EntityBuild build)
    {
        if (flag is null)
        {
            return null;
        }

        if (!flag.IsExpression)
        {
            return flag.Boolean == true ? FieldMask.Always : null;
        }

        var path = $"/entities/{build.Name}/fields/{fieldName}/{flagName}";
        var compiled = CompileRuleExpression(flag.Expression!, path, build);
        if (compiled is null)
        {
            return null;
        }

        if (!ReferencesRowField(compiled.Root))
        {
            return FieldMask.FromExpression(compiled);
        }

        build.Errors.Add(RowDependentMaskError(path, flagName));
        return null;
    }

    /// <summary>Builds the "row-dependent mask" rejection: the one place this message/fix pair is assembled.</summary>
    private static DescriptorValidationError RowDependentMaskError(string path, string flagName) => new(
        path,
        $"A '{flagName}' expression must not reference row fields; it is evaluated once per request against the caller/tenant context only.",
        "Row-dependent masking would require post-processing the returned rows, which the 'no in-memory post-filter' invariant forbids. Use a static true/false, or defer the row-dependent check to a future post-processing feature.",
        DescriptorValidationSeverity.Error);

    /// <summary>
    /// Walks a compiled tree for any reference to a row field — the construct <c>hidden</c>/<c>readOnly</c>
    /// must never contain. Deny-by-default: only a literal or a caller/tenant context reference is
    /// provably context-only, every composite recurses, and any other node kind — a row field, <c>has()</c>,
    /// <c>changed(...)</c>, or a future construct this walk was never updated for — counts as
    /// row-dependent rather than silently passing as safe.
    /// </summary>
    internal static bool ReferencesRowField(CelNode node) => node switch
    {
        CelLiteral or CelContextRef => false,
        CelUnary unary => ReferencesRowField(unary.Operand),
        CelBinary binary => ReferencesRowField(binary.Left) || ReferencesRowField(binary.Right),
        CelConditional conditional =>
            ReferencesRowField(conditional.Condition) || ReferencesRowField(conditional.WhenTrue) || ReferencesRowField(conditional.WhenFalse),
        _ => true,
    };

    private static DescriptorValidationError Error(string path, CelCompilationError error) =>
        new(path, error.Message, error.FixSuggestion, DescriptorValidationSeverity.Error);
}
