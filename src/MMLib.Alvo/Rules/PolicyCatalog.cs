using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules;

/// <summary>
/// Every entity's compiled policy, built once from a descriptor and its mapped schema: per
/// operation, the compiled <c>USING</c>/<c>WITH CHECK</c> predicates; the synthesized tenant scope;
/// and the compiled <c>hidden</c>/<c>readOnly</c> field flags. Compiling every rule here — rather
/// than lazily, per request — is what makes "a rule referencing a nonexistent column fails at save,
/// not at request time" true.
/// </summary>
/// <remarks>
/// <see cref="Build"/>/<see cref="TryBuild"/> compile rules for exactly the entities present in the
/// <c>schema</c> argument, not every entity the <c>descriptor</c> argument declares. This is correct
/// for a descriptor entity that is legitimately absent from the mapped schema (a dynamic-storage
/// entity, filtered out by <c>DescriptorToSchemaMapper</c>, which this compiler does not yet police),
/// but it means a descriptor entity with no matching schema entry for some other reason — a caller
/// building the two arguments by hand, inconsistently, rather than from
/// <c>DescriptorToSchemaMapper.Map(descriptor)</c> — compiles with no error for rules on that entity: a
/// mismatch neither <see cref="Build"/> nor <see cref="TryBuild"/> can detect from these two arguments
/// alone. Callers must keep <c>schema</c> consistent with <c>descriptor</c> (in practice, by passing
/// <c>DescriptorToSchemaMapper.Map(descriptor)</c>'s own output), which every call site in this
/// codebase does by construction.
/// </remarks>
public sealed class PolicyCatalog
{
    private readonly IReadOnlyDictionary<string, EntityPolicy> _entities;

    /// <summary>Initializes a new instance of the <see cref="PolicyCatalog"/> class.</summary>
    /// <param name="entities">The compiled per-entity policy, keyed by entity name.</param>
    /// <param name="roles">The project's declared roles, built from the same descriptor.</param>
    internal PolicyCatalog(IReadOnlyDictionary<string, EntityPolicy> entities, RoleCatalog roles)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(roles);
        _entities = entities;
        Roles = roles;
    }

    /// <summary>
    /// Gets the roles this project recognises: the built-ins plus the descriptor's <c>auth.roles</c>.
    /// </summary>
    /// <remarks>
    /// It rides on the catalog rather than on a provider of its own because the descriptor is the one
    /// source of truth for both halves of authorization and they must never disagree: the same
    /// declaration a rule's role literal is validated against is the one authentication may mint a
    /// <see cref="Role"/> from, primed at the same instant, from the same descriptor, behind the same
    /// project-identity guard. A second, independently primed holder could serve a role set the rules
    /// were never compiled against.
    /// </remarks>
    public RoleCatalog Roles { get; }

    /// <summary>Builds a <see cref="PolicyCatalog"/> from a descriptor and its mapped schema.</summary>
    /// <param name="descriptor">The project descriptor whose <c>rules</c>/<c>hidden</c>/<c>readOnly</c> are compiled.</param>
    /// <param name="schema">The schema <paramref name="descriptor"/> maps to, every rule is checked against.</param>
    /// <param name="compiler">The CEL compiler every rule and field flag is compiled through.</param>
    /// <returns>The built catalog.</returns>
    /// <exception cref="DescriptorValidationException">Any rule, tenant scope, or field flag failed to compile.</exception>
    public static PolicyCatalog Build(AlvoDescriptor descriptor, SchemaModel schema, ICelCompiler compiler)
    {
        if (!TryBuild(descriptor, schema, compiler, out var catalog, out var errors))
        {
            throw new DescriptorValidationException(new DescriptorValidationResult(errors));
        }

        return catalog!;
    }

    /// <summary>Attempts to build a <see cref="PolicyCatalog"/> from a descriptor and its mapped schema.</summary>
    /// <param name="descriptor">The project descriptor whose <c>rules</c>/<c>hidden</c>/<c>readOnly</c> are compiled.</param>
    /// <param name="schema">The schema <paramref name="descriptor"/> maps to, every rule is checked against.</param>
    /// <param name="compiler">The CEL compiler every rule and field flag is compiled through.</param>
    /// <param name="catalog">The built catalog, when every rule compiled; otherwise <see langword="null"/>.</param>
    /// <param name="errors">Every compilation problem found, agent-first with a JSON path and a fix suggestion; empty on success.</param>
    /// <returns><see langword="true"/> when every rule, tenant scope, and field flag compiled.</returns>
    public static bool TryBuild(
        AlvoDescriptor descriptor,
        SchemaModel schema,
        ICelCompiler compiler,
        out PolicyCatalog? catalog,
        out IReadOnlyList<DescriptorValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(compiler);
        return PolicyCatalogBuilder.TryBuild(descriptor, schema, compiler, out catalog, out errors);
    }

    /// <summary>Looks up an entity's compiled policy.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="policy">The entity's compiled policy, when found.</param>
    /// <returns><see langword="true"/> when <paramref name="entity"/> is known to this catalog.</returns>
    internal bool TryGetEntity(string entity, out EntityPolicy policy) => _entities.TryGetValue(entity, out policy!);
}

/// <summary>One entity's compiled policy: its tenancy, the per-operation predicates, and the field masks.</summary>
/// <param name="Tenancy">The entity's tenancy mode, mirrored from <see cref="EntitySchema.Tenancy"/>.</param>
/// <param name="TenantScope">The synthesized tenant scope, or <see langword="null"/> on a global entity.</param>
/// <param name="Operations">The compiled <c>USING</c>/<c>WITH CHECK</c> pair for every <see cref="DataOperation"/>.</param>
/// <param name="Hidden">The compiled <c>hidden</c> flag for every field that declares one, keyed by field name.</param>
/// <param name="ReadOnly">The compiled <c>readOnly</c> flag for every field that declares one, keyed by field name.</param>
internal sealed record EntityPolicy(
    TenancyMode? Tenancy,
    CompiledExpression? TenantScope,
    IReadOnlyDictionary<DataOperation, OperationPolicy> Operations,
    IReadOnlyDictionary<string, FieldMask> Hidden,
    IReadOnlyDictionary<string, FieldMask> ReadOnly);

/// <summary>
/// The compiled <c>USING</c>/<c>WITH CHECK</c> pair for one operation. Exactly Postgres's
/// <c>CREATE POLICY</c> mapping: <c>list</c>/<c>get</c>/<c>delete</c> carry <see cref="Using"/> only,
/// <c>create</c> carries <see cref="WithCheck"/> only, and <c>update</c> carries both — built from the
/// same compiled expression instance, never two independently compiled ones.
/// </summary>
/// <param name="Using">The <c>USING</c>-equivalent predicate, or <see langword="null"/> when this operation has none configured or does not consult one.</param>
/// <param name="WithCheck">The <c>WITH CHECK</c>-equivalent predicate, or <see langword="null"/> when this operation has none configured or does not consult one.</param>
/// <param name="Required">
/// The caller/tenant context values any of this operation's predicates — <see cref="Using"/>,
/// <see cref="WithCheck"/>, or the entity's <see cref="EntityPolicy.TenantScope"/> — actually reads.
/// </param>
internal sealed record OperationPolicy(
    CompiledExpression? Using,
    CompiledExpression? WithCheck,
    RequiredContext Required);

/// <summary>
/// The caller/tenant context values one compiled expression — or a set of them — actually reads,
/// precomputed at apply time by walking the compiled tree, never per request and never by
/// re-parsing the source. The one shape both halves of the required-context gate are expressed in:
/// an operation's predicates (where a missing value <b>denies the call</b>) and a
/// <c>hidden</c>/<c>readOnly</c> mask (where it <b>keeps the field masked</b>).
/// </summary>
/// <remarks>
/// Both directions exist because neither backend can be trusted to answer a comparison against an
/// operand the caller never supplied: an absent value resolves to <see langword="null"/> and
/// collapses the comparison to <see langword="false"/>, which a negation inverts into "every row"
/// for a predicate and which a positive-form mask reads as "this field is visible". Neither is a
/// safe answer, so the value is refused upstream in both channels rather than folded.
/// </remarks>
/// <param name="TenantId">Whether <c>@tenant.id</c> is read.</param>
/// <param name="UserId">Whether <c>@user.id</c> is read.</param>
internal readonly record struct RequiredContext(bool TenantId, bool UserId)
{
    /// <summary>An expression that reads no caller/tenant context value at all.</summary>
    public static RequiredContext None { get; } = new(false, false);

    /// <summary>Whether this expression reads <c>@tenant.id</c> and <paramref name="context"/> carries no tenant.</summary>
    /// <param name="context">The caller resolving against the expression.</param>
    public bool TenantIdMissingFrom(AlvoContext context) => TenantId && context.Tenant is null;

    /// <summary>Whether this expression reads <c>@user.id</c> and <paramref name="context"/> carries no identity.</summary>
    /// <param name="context">The caller resolving against the expression.</param>
    public bool UserIdMissingFrom(AlvoContext context) => UserId && HasNoIdentity(context);

    /// <summary>Whether <paramref name="context"/> is missing either value this expression reads.</summary>
    /// <param name="context">The caller resolving against the expression.</param>
    public bool IsMissingFrom(AlvoContext context) =>
        TenantIdMissingFrom(context) || UserIdMissingFrom(context);

    /// <summary>
    /// The reserved all-zero <see cref="UserId"/> means "no identity" (see
    /// <see cref="AlvoContext.Anonymous"/>) — never a real caller who happens to own the all-zero
    /// rows.
    /// </summary>
    private static bool HasNoIdentity(AlvoContext context) => context.User.Value == Guid.Empty;
}

/// <summary>
/// A compiled <c>hidden</c>/<c>readOnly</c> field flag: either always on (a static <see langword="true"/>),
/// or a context-only CEL expression evaluated once per <see cref="IPolicyEngine.Resolve"/> call. A
/// static <see langword="false"/> or an absent flag never produces a <see cref="FieldMask"/> at all —
/// the field simply has no entry in the catalog's <c>Hidden</c>/<c>ReadOnly</c> map.
/// </summary>
internal readonly record struct FieldMask
{
    private FieldMask(bool alwaysOn, CompiledExpression? expression, RequiredContext required)
    {
        AlwaysOn = alwaysOn;
        Expression = expression;
        Required = required;
    }

    /// <summary>Gets a value indicating whether this field is always in the mask, regardless of caller.</summary>
    public bool AlwaysOn { get; }

    /// <summary>Gets the compiled context-only expression to evaluate per request, when <see cref="AlwaysOn"/> is <see langword="false"/>.</summary>
    public CompiledExpression? Expression { get; }

    /// <summary>
    /// Gets the caller/tenant context values <see cref="Expression"/> reads. A caller missing one of
    /// them leaves the field masked without the expression ever being evaluated — the fail-closed
    /// direction for a mask, since evaluating it would collapse the absent operand to
    /// <see langword="false"/> and report a hidden field visible or a frozen field writable.
    /// </summary>
    public RequiredContext Required { get; }

    /// <summary>A flag that is always on (the descriptor declared a static <see langword="true"/>).</summary>
    public static FieldMask Always { get; } = new(true, null, RequiredContext.None);

    /// <summary>Creates a flag evaluated per request from a compiled context-only expression.</summary>
    /// <param name="expression">The compiled, context-only Rule-profile expression.</param>
    /// <param name="required">The context values <paramref name="expression"/> reads.</param>
    public static FieldMask FromExpression(CompiledExpression expression, RequiredContext required)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new FieldMask(false, expression, required);
    }
}
