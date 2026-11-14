using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tenancy.SelfService;

/// <summary>
/// The apply-time warning of spec §3.1 (plan D12): with <c>auth.signUp.mode: open</c>, every write rule of a
/// <c>tenancy: global</c> entity that admits <c>authenticated</c> is listed — those shared rows are now writable by
/// anyone who signs up.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Admits <c>authenticated</c>" is read as: the rule's role literals name it</b>
/// (<see cref="PolicyCatalogBuilder.RoleLiterals"/>). A rule that admits without naming a role —
/// <c>created_by == @user.id</c> — admits each person to their own rows only, and is not listed. "Global" is read as
/// a project with <c>tenancy.enabled</c>, so a level-1 app (no tenancy) draws none even where an entity says
/// <c>tenancy: global</c> — there every row is shared and every write rule would be listed, which is the noise S2 D17
/// declined. A <c>list</c>/<c>get</c> rule is a read and draws none.
/// </para>
/// <para>
/// <b>Only ever a warning, in a pass of its own</b>, the <see cref="OwnerComparisonCheck"/> shape: each source is
/// compiled a second time through the same compiler and <see cref="CelProfile.Rule"/> profile the catalog uses, per
/// apply. A source that does not compile draws nothing — its error is already reported.
/// </para>
/// </remarks>
internal static class GlobalWriteWarnings
{
    /// <summary>One warning per write rule of a global entity that admits <c>authenticated</c>, under open sign-up.</summary>
    /// <param name="descriptor">The parsed descriptor, whose sign-up mode and rules are read.</param>
    /// <param name="schema">The mapped schema, which says which entities are global.</param>
    /// <param name="compiler">The compiler the catalog compiles the same rules with.</param>
    internal static IReadOnlyList<DescriptorValidationError> Warnings(AlvoDescriptor descriptor, SchemaModel schema, ICelCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(compiler);
        if (descriptor.Auth?.SignUp?.Mode != SignUpMode.Open || descriptor.Tenancy?.Enabled != true)
        {
            return [];
        }

        return
        [
            .. from entity in schema.Entities
               where entity.Tenancy == TenancyMode.Global
               let rules = descriptor.Entities.GetValueOrDefault(entity.Name)?.Rules
               where rules is not null
               from write in Writes(rules)
               where AdmitsAuthenticated(write.Source, entity, compiler)
               select Warning(entity.Name, write.Operation),
        ];
    }

    private static IEnumerable<(string Operation, string Source)> Writes(AccessRules rules)
    {
        (string Operation, string? Source)[] writes = [("create", rules.Create), ("update", rules.Update), ("delete", rules.Delete)];
        return writes.Where(write => write.Source is not null).Select(write => (write.Operation, write.Source!));
    }

    private static bool AdmitsAuthenticated(string source, EntitySchema entity, ICelCompiler compiler)
    {
        var result = compiler.Compile(source, CelProfile.Rule, entity);
        return result.IsSuccess
            && PolicyCatalogBuilder.RoleLiterals(result.Expression!.Root).Contains(Role.Authenticated.Name, StringComparer.Ordinal);
    }

    private static DescriptorValidationError Warning(string entity, string operation) => new(
        $"/entities/{entity}/rules/{operation}",
        $"'{entity}' is tenancy: global and its {operation} rule admits 'authenticated'; with auth.signUp.mode open, "
        + $"anyone who signs up can {operation} these shared rows.",
        $"Admit a project role an operator grants instead, or make '{entity}' tenancy: scoped.",
        DescriptorValidationSeverity.Warning);
}
