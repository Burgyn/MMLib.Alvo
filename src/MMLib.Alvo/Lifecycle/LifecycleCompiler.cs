using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// Compiles an entity's <c>stateMachine</c> and <c>timers</c> in the policy catalog's own pass: a transition's roles against
/// the declared roles and its <c>when</c> as a <see cref="CelProfile.Condition"/>; a timer's <c>at</c> as a
/// <see cref="CelValueType.Timestamp"/> and its <c>when</c> as a condition, both over the record's own fields only.
/// </summary>
/// <remarks>
/// <b>A timer reads its row and nothing else.</b> It is re-judged at every write by whichever caller made it, so an
/// <c>old.</c>/<c>new.</c>/<c>changed()</c> read would arm it from one write's comparison, and an <c>@user</c>/<c>@tenant</c>
/// read from one caller's identity — neither is a property of the record. The shape checks are
/// <see cref="StateMachineCheck"/>'s and <see cref="EntityTimerCheck"/>'s.
/// </remarks>
internal static class LifecycleCompiler
{
    /// <summary>The entity's compiled lifecycle, or <see cref="EntityLifecycle.None"/> when it declares neither member.</summary>
    /// <param name="descriptor">The entity's descriptor, if any.</param>
    /// <param name="scope">What the entity compiles against.</param>
    internal static EntityLifecycle Compile(EntityDescriptor? descriptor, LifecycleScope scope)
    {
        if (descriptor is null || (descriptor.StateMachine is null && descriptor.Timers is not { Count: > 0 }))
        {
            return EntityLifecycle.None;
        }

        return new EntityLifecycle(CompileMachine(descriptor, scope), CompileTimers(descriptor.Timers, scope));
    }

    private static CompiledStateMachine? CompileMachine(EntityDescriptor descriptor, LifecycleScope scope)
    {
        if (descriptor.StateMachine is not { } machine)
        {
            return null;
        }

        var path = $"{scope.Path}/stateMachine/transitions";
        return new CompiledStateMachine(
            machine.Field,
            machine.Initial,
            StateMachineCheck.DefaultStateOf(descriptor, machine.Field),
            [.. machine.Transitions.Select(transition => CompileTransition(transition.Key, transition.Value, $"{path}/{transition.Key}", scope))]);
    }

    private static CompiledTransition CompileTransition(string name, StateTransition transition, string path, LifecycleScope scope) => new(
        name,
        transition.From is [StateMachineCheck.AnyState] ? null : transition.From.ToHashSet(StringComparer.Ordinal),
        transition.To,
        transition.Roles is null ? null : ResolveRoles(transition.Roles, $"{path}/roles", scope),
        transition.When is null ? null : CompileWhen(transition.When, $"{path}/when", scope));

    private static HashSet<Role> ResolveRoles(IReadOnlyList<string> names, string path, LifecycleScope scope)
    {
        var roles = new HashSet<Role>();
        for (var position = 0; position < names.Count; position++)
        {
            if (scope.Roles.TryGet(names[position], out var role))
            {
                roles.Add(role);
                continue;
            }

            scope.Errors.Add(PolicyCatalogBuilder.UndeclaredRoleError($"{path}/{position}", names[position], scope.Roles));
        }

        return roles;
    }

    private static CompiledExpression? CompileWhen(string source, string path, LifecycleScope scope)
    {
        var compiled = Accepted(scope.Compiler.Compile(source, CelProfile.Condition, scope.Schema), path, scope);
        var undeclared = compiled is null ? [] : PolicyCatalogBuilder.RoleLiterals(compiled.Root).Where(role => !scope.Roles.TryGet(role, out _)).ToList();
        scope.Errors.AddRange(undeclared.Select(role => PolicyCatalogBuilder.UndeclaredRoleError(path, role, scope.Roles)));

        return undeclared.Count == 0 ? compiled : null;
    }

    private static List<CompiledTimer> CompileTimers(IReadOnlyDictionary<string, EntityTimer>? timers, LifecycleScope scope) =>
        timers is null
            ? []
            : [.. timers.Select(timer => CompileTimer(timer.Key, timer.Value, $"{scope.Path}/timers/{timer.Key}", scope)).OfType<CompiledTimer>()];

    private static CompiledTimer? CompileTimer(string name, EntityTimer timer, string path, LifecycleScope scope)
    {
        var at = OwnFieldsOnly(CompileAt(timer.At, $"{path}/at", scope), $"{path}/at", scope);
        var when = timer.When is null
            ? null
            : OwnFieldsOnly(Accepted(scope.Compiler.Compile(timer.When, CelProfile.Condition, scope.Schema), $"{path}/when", scope), $"{path}/when", scope);

        return at is null ? null : new CompiledTimer(name, at, when, EntityTimerCheck.EventOf(scope.Schema.Name, name, timer));
    }

    private static CompiledExpression? CompileAt(string source, string path, LifecycleScope scope)
    {
        if (scope.Compiler is not IValueCompiler values)
        {
            scope.Errors.Add(Error(
                path,
                "Timers need Alvo's own CEL compiler, and this host replaced it: a timer's 'at' is a timestamp, which only Alvo's compiler can type.",
                "Remove the ICelCompiler registration that replaces Alvo's, or remove 'timers'."));
            return null;
        }

        return Accepted(values.CompileValue(source, CelProfile.Condition, scope.Schema, CelValueType.Timestamp), path, scope);
    }

    private static CompiledExpression? OwnFieldsOnly(CompiledExpression? compiled, string path, LifecycleScope scope)
    {
        if (compiled is null)
        {
            return null;
        }

        var refusal = ForeignRead(compiled.Root);
        if (refusal is not null)
        {
            scope.Errors.Add(Error(path, refusal, "Read the record's fields by their bare names, e.g. starts_at, and nothing about the caller."));
        }

        return refusal is null ? compiled : null;
    }

    private static string? ForeignRead(CelNode root) =>
        CelImageReads.Reads(root, CelRecordState.Old) || CelImageReads.Reads(root, CelRecordState.New) || CelImageReads.ReadsChanged(root)
            ? "A timer reads the record's own fields only: 'old.', 'new.' and 'changed()' compare one write's images, and a timer is re-judged at every write of the record."
            : CelImageReads.ReadsContext(root)
                ? "A timer reads the record's own fields only, never the caller or the tenant ('@user', '@tenant'): it is re-judged at every write of the record, by whichever caller makes it."
                : null;

    private static CompiledExpression? Accepted(CelCompilationResult result, string path, LifecycleScope scope)
    {
        scope.Errors.AddRange(result.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
        return result.IsSuccess ? result.Expression : null;
    }

    private static DescriptorValidationError Error(string path, string message, string? fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}

/// <summary>What one entity's lifecycle compiles against — the policy catalog builder's own inputs and error list.</summary>
/// <param name="Schema">The entity every expression is type-checked against.</param>
/// <param name="Compiler">The CEL compiler every expression goes through.</param>
/// <param name="Roles">The project's declared roles.</param>
/// <param name="Path">The entity's JSON pointer, such as <c>/entities/bookings</c>.</param>
/// <param name="Errors">The accumulator every problem is appended to.</param>
internal sealed record LifecycleScope(
    EntitySchema Schema, ICelCompiler Compiler, RoleCatalog Roles, string Path, List<DescriptorValidationError> Errors);
