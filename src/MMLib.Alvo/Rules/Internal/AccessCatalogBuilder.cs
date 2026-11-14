using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Compiles the descriptor's three <c>access</c> levels, in <see cref="PolicyCatalogBuilder"/>'s own
/// pass and against its own <see cref="RoleCatalog"/>.
/// </summary>
/// <remarks>
/// <b>Here rather than in its own pass, and that is the point.</b> One apply compiles the rules and the
/// levels together, validates every role literal in both against one declared set, and publishes one
/// catalogue — so a level and a rule can never be judged against two descriptor revisions, and a role
/// removed from <c>auth.roles</c> is refused in both places at once.
/// <para>
/// <b>A tenant role never manages the project</b> (spec §3.1, §6.2): a role literal naming one of
/// <c>tenancy.selfService.assignableRoles</c> refuses its level, because anyone who creates a tenant holds the
/// creator's roles there. A declared role that is not assignable — <c>owner</c> in an existing descriptor — still
/// manages, exactly as before.
/// </para>
/// </remarks>
internal static class AccessCatalogBuilder
{
    /// <summary>
    /// Gets the entity an access level is type-checked against: one with no fields, because an access
    /// level reads no row.
    /// </summary>
    /// <remarks>
    /// The <see cref="CelProfile.Access"/> profile refuses every field reference before the checker
    /// resolves a name, so this schema's emptiness is never the thing that produces the error — the
    /// profile is. It exists because <see cref="ICelCompiler.Compile(string, CelProfile, EntitySchema)"/> takes an entity, and inventing an
    /// overload that does not would widen a published port for one caller.
    /// </remarks>
    internal static EntitySchema ProjectScope { get; } = new() { Name = "<project>", Fields = [] };

    /// <summary>Compiles every declared level, appending every problem found rather than the first.</summary>
    /// <param name="access">The descriptor's <c>access</c> block, or <see langword="null"/>.</param>
    /// <param name="compiler">The CEL compiler every level goes through.</param>
    /// <param name="roles">The project's declared roles, for validating role literals.</param>
    /// <param name="tenantRoles">The tenant roles (<c>tenancy.selfService.assignableRoles</c>), none of which may manage.</param>
    /// <param name="errors">The shared accumulator every problem is appended to.</param>
    /// <returns>The compiled levels, each <see langword="null"/> when undeclared or refused.</returns>
    internal static ManagementAccessCatalog Build(
        Access? access,
        ICelCompiler compiler,
        RoleCatalog roles,
        IReadOnlySet<Role> tenantRoles,
        List<DescriptorValidationError> errors)
    {
        if (access is null)
        {
            return ManagementAccessCatalog.Empty;
        }

        var scope = new LevelScope(compiler, roles, tenantRoles, errors);
        return new ManagementAccessCatalog(
            CompileLevel(access.Admin, "admin", scope),
            CompileLevel(access.Developer, "developer", scope),
            CompileLevel(access.Viewer, "viewer", scope));
    }

    private static CompiledExpression? CompileLevel(string? source, string level, LevelScope scope)
    {
        if (source is null)
        {
            return null;
        }

        var path = $"/access/{level}";
        var result = scope.Compiler.Compile(source, CelProfile.Access, ProjectScope);
        if (!result.IsSuccess)
        {
            scope.Errors.AddRange(result.Errors.Select(
                error => new DescriptorValidationError(
                    path, error.Message, error.FixSuggestion, DescriptorValidationSeverity.Error)));
            return null;
        }

        return HasProjectRolesOnly(result.Expression!, path, scope) ? result.Expression : null;
    }

    /// <summary>
    /// Whether every role literal is declared and none is a tenant role; one refusal per offending literal, the
    /// undeclared one first, since an undeclared name is not a tenant role either.
    /// </summary>
    private static bool HasProjectRolesOnly(CompiledExpression expression, string path, LevelScope scope)
    {
        var refusals = PolicyCatalogBuilder.RoleLiterals(expression.Root)
            .Select(role => RoleRefusal(role, path, scope))
            .OfType<DescriptorValidationError>()
            .ToList();

        scope.Errors.AddRange(refusals);
        return refusals.Count == 0;
    }

    private static DescriptorValidationError? RoleRefusal(string role, string path, LevelScope scope)
    {
        if (!scope.Roles.TryGet(role, out var declared))
        {
            return PolicyCatalogBuilder.UndeclaredRoleError(path, role, scope.Roles);
        }

        return scope.TenantRoles.Contains(declared) ? TenantRoleError(path, role) : null;
    }

    private static DescriptorValidationError TenantRoleError(string path, string role) => new(
        path,
        $"'{role}' is a tenant role (tenancy.selfService.assignableRoles), and a tenant role can never manage the project.",
        $"Admit a project role here — one an operator or an API key grants — or remove '{role}' from assignableRoles.",
        DescriptorValidationSeverity.Error);

    /// <summary>What every level is compiled and judged against, in one apply.</summary>
    /// <param name="Compiler">The CEL compiler every level goes through.</param>
    /// <param name="Roles">The project's declared roles.</param>
    /// <param name="TenantRoles">The tenant roles, none of which may manage.</param>
    /// <param name="Errors">The shared accumulator every problem is appended to.</param>
    private sealed record LevelScope(
        ICelCompiler Compiler, RoleCatalog Roles, IReadOnlySet<Role> TenantRoles, List<DescriptorValidationError> Errors);
}
