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
    /// profile is. It exists because <see cref="ICelCompiler.Compile"/> takes an entity, and inventing an
    /// overload that does not would widen a published port for one caller.
    /// </remarks>
    internal static EntitySchema ProjectScope { get; } = new() { Name = "<project>", Fields = [] };

    /// <summary>Compiles every declared level, appending every problem found rather than the first.</summary>
    /// <param name="access">The descriptor's <c>access</c> block, or <see langword="null"/>.</param>
    /// <param name="compiler">The CEL compiler every level goes through.</param>
    /// <param name="roles">The project's declared roles, for validating role literals.</param>
    /// <param name="errors">The shared accumulator every problem is appended to.</param>
    /// <returns>The compiled levels, each <see langword="null"/> when undeclared or refused.</returns>
    internal static ManagementAccessCatalog Build(
        Access? access,
        ICelCompiler compiler,
        RoleCatalog roles,
        List<DescriptorValidationError> errors)
    {
        if (access is null)
        {
            return ManagementAccessCatalog.Empty;
        }

        return new ManagementAccessCatalog(
            CompileLevel(access.Admin, "admin", compiler, roles, errors),
            CompileLevel(access.Developer, "developer", compiler, roles, errors),
            CompileLevel(access.Viewer, "viewer", compiler, roles, errors));
    }

    private static CompiledExpression? CompileLevel(
        string? source,
        string level,
        ICelCompiler compiler,
        RoleCatalog roles,
        List<DescriptorValidationError> errors)
    {
        if (source is null)
        {
            return null;
        }

        var path = $"/access/{level}";
        var result = compiler.Compile(source, CelProfile.Access, ProjectScope);
        if (!result.IsSuccess)
        {
            errors.AddRange(result.Errors.Select(
                error => new DescriptorValidationError(
                    path, error.Message, error.FixSuggestion, DescriptorValidationSeverity.Error)));
            return null;
        }

        return HasDeclaredRoles(result.Expression!, path, roles, errors) ? result.Expression : null;
    }

    private static bool HasDeclaredRoles(
        CompiledExpression expression, string path, RoleCatalog roles, List<DescriptorValidationError> errors)
    {
        var undeclared = PolicyCatalogBuilder.RoleLiterals(expression.Root)
            .Where(role => !roles.TryGet(role, out _))
            .ToList();

        errors.AddRange(undeclared.Select(role => PolicyCatalogBuilder.UndeclaredRoleError(path, role, roles)));
        return undeclared.Count == 0;
    }
}
