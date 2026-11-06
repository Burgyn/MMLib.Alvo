using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Compiles one entity's <c>after*</c> hooks: the condition through <see cref="CelProfile.Condition"/>, every
/// <c>{{…}}</c> template parsed and validated against the entity's schema, and every reference to a webhook
/// endpoint or a message template resolved — all at <b>apply</b> time, into the
/// <see cref="Rules.PolicyCatalog"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It runs inside the policy catalog's own pass, and that is the design.</b> <see cref="EntitySchema"/> and
/// <see cref="SchemaModel"/> carry no hooks, so a hook catalog had two possible homes: the policy catalog's
/// existing priming or a fourth, independently primed holder. The second is the failure
/// <see cref="IPolicyCatalogProvider"/>'s remarks were written to prevent — it would mean a hook compiled
/// against a different schema revision than the rules judging the same write. So this type has exactly one
/// caller, <c>PolicyCatalogBuilder</c>, and shares its error accumulator: one pass, one priming site, one set
/// of errors.
/// </para>
/// <para>
/// <b>Everything is resolved here so that nothing is resolved at delivery.</b> At delivery there is nobody to
/// report a refusal to — a webhook that cannot render its body is a delivery that fails forever, retried
/// until it hits the attempt ceiling, and an author reading that sees an endpoint problem rather than a typo.
/// So a placeholder naming an undeclared field, an endpoint no <c>webhooks.endpoints</c> entry declares and a
/// template name no <c>templates</c> entry declares are all refused when the descriptor is applied.
/// </para>
/// <para>
/// <b>A refusal points at the slot an author edits.</b> An action's own slots hang off the hook
/// (<c>/entities/deals/hooks/afterUpdate/0/action/to</c>); a message template's <c>subject</c>/<c>body</c>
/// point at the template (<c>/templates/deal-won/body</c>), because that is the file position of the mistake
/// and because one template can be referenced from several entities — in which case it is validated once per
/// referencing entity, against that entity's schema.
/// </para>
/// </remarks>
internal static class AfterHookCompiler
{
    /// <summary>Compiles the three <c>after*</c> lists an entity declares, appending every problem to the scope.</summary>
    /// <param name="hooks">The entity's declared hooks, or <see langword="null"/> when it declares none.</param>
    /// <param name="scope">The schema, compiler, project-level references, pointer prefix and error accumulator.</param>
    /// <returns>
    /// The compiled catalog, or <see cref="EntityAfterHooks.None"/> when the entity declares no after-hook at
    /// all. A hook that failed to compile is absent from the result <em>and</em> present in the errors, so a
    /// catalog is never built holding a half-compiled action.
    /// </returns>
    internal static EntityAfterHooks Compile(EntityHooks? hooks, AfterHookScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (hooks is null)
        {
            return EntityAfterHooks.None;
        }

        var afterCreate = CompilePoint(AfterCreatePoint, hooks.AfterCreate, scope);
        var afterUpdate = CompilePoint(AfterUpdatePoint, hooks.AfterUpdate, scope);
        var afterDelete = CompilePoint(AfterDeletePoint, hooks.AfterDelete, scope);

        return afterCreate.Count + afterUpdate.Count + afterDelete.Count == 0
            ? EntityAfterHooks.None
            : new EntityAfterHooks(afterCreate, afterUpdate, afterDelete);
    }

    private const string AfterCreatePoint = "afterCreate";
    private const string AfterUpdatePoint = "afterUpdate";
    private const string AfterDeletePoint = "afterDelete";

    private const string ActionSegment = "/action";
    private const string ConditionSlot = "condition";

    private static List<CompiledAfterHook> CompilePoint(
        string point, IReadOnlyList<AfterHook>? declared, AfterHookScope scope)
    {
        if (declared is null || declared.Count == 0)
        {
            return [];
        }

        var compiled = new List<CompiledAfterHook>(declared.Count);
        var pointScope = scope with { Point = point };
        for (var index = 0; index < declared.Count; index++)
        {
            var hook = CompileHook(declared[index], $"{scope.EntityPath}/hooks/{point}/{index}", pointScope);
            if (hook is not null)
            {
                compiled.Add(hook);
            }
        }

        return compiled;
    }

    private static CompiledAfterHook? CompileHook(AfterHook hook, string path, AfterHookScope scope)
    {
        var condition = CompileCondition(hook.Condition, $"{path}/{ConditionSlot}", scope);
        if (hook.Condition is not null && condition is null)
        {
            return null;
        }

        var action = CompileAction(hook.Action, $"{path}{ActionSegment}", scope);

        return action is null
            ? null
            : new CompiledAfterHook(
                path, condition, EventConditionReads.Required(condition), action, EventConditionReads.ReadsRoles(condition));
    }

    /// <summary>
    /// Compiles a hook condition in the <see cref="CelProfile.Condition"/> profile — the only profile where
    /// <c>old.</c>, <c>new.</c> and <c>changed(field)</c> are legal, which is what an after-hook condition is
    /// written in — and then refuses, through <see cref="AfterImagePhase"/>, any row image the point's event does not
    /// carry (#325), and any field the entity leaves out of its event images (<c>events.exclude</c>).
    /// </summary>
    private static CompiledExpression? CompileCondition(string? source, string path, AfterHookScope scope)
    {
        if (source is null)
        {
            return null;
        }

        var result = scope.Compiler.Compile(source, CelProfile.Condition, scope.Schema);
        if (!result.IsSuccess)
        {
            scope.Errors.AddRange(result.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
            return null;
        }

        var expression = result.Expression!;
        IReadOnlyList<DescriptorValidationError> refusals =
        [
            .. AfterImagePhase.Refusals(expression.Root, AfterImages.At(scope.Point), scope.Point, path),
            .. ExcludedFieldReads(expression.Root, path, scope),
        ];
        scope.Errors.AddRange(refusals);

        return refusals.Count == 0 ? expression : null;
    }

    private static CompiledAction? CompileAction(AutomationAction action, string path, AfterHookScope scope) =>
        ActionCompiler.Compile(action, path, scope);

    private static IEnumerable<DescriptorValidationError> ExcludedFieldReads(
        CelNode root, string path, AfterHookScope scope) =>
        EventConditionReads.ExcludedFieldsRead(root, scope.Schema)
            .Select(field => Error(path, ExcludedCondition(field), ExcludedConditionFix(scope, field)));

    private static string ExcludedCondition(string field) =>
        $"This after-hook condition reads '{field}', which this entity leaves out of its event images "
        + "(events.exclude), so the reference would read null on every event and the comparison would be decided by "
        + "its absence — silently, and not always in the denying direction.";

    private static string ExcludedConditionFix(AfterHookScope scope, string field) =>
        $"Remove '{field}' from {scope.EntityPath}/events/exclude, or stop reading it here; 'changed({field})' still "
        + "names an excluded field.";

    private static DescriptorValidationError Error(string path, string message, string? fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}

/// <summary>
/// Everything one entity's after-hooks compile against: its schema, the CEL compiler, the project-level
/// blocks an action may reference, the entity's own JSON pointer, and the shared error accumulator.
/// </summary>
/// <remarks>
/// Bundled so every helper reads as "compile this slot at this path" rather than re-threading six arguments
/// that never vary within an entity — the same reason <c>PolicyCatalogBuilder</c> bundles its own.
/// </remarks>
/// <param name="Schema">The entity every condition and placeholder is checked against.</param>
/// <param name="Compiler">The CEL compiler every condition goes through.</param>
/// <param name="Templates">The descriptor's <c>templates</c>, or empty when it declares none.</param>
/// <param name="Endpoints">The descriptor's <c>webhooks.endpoints</c>, or empty when it declares none.</param>
/// <param name="EntityPath">The entity's JSON pointer, such as <c>/entities/deals</c>.</param>
/// <param name="Errors">The accumulator every problem is appended to — the policy catalog builder's own.</param>
internal sealed record AfterHookScope(
    EntitySchema Schema,
    ICelCompiler Compiler,
    IReadOnlyDictionary<string, MessageTemplate> Templates,
    IReadOnlyDictionary<string, WebhookEndpoint> Endpoints,
    string EntityPath,
    List<DescriptorValidationError> Errors)
{
    /// <summary>
    /// The after-point being compiled, such as <c>afterCreate</c>; set per point by the compiler, and it decides which
    /// row images a condition or template may read (<see cref="AfterImages.At"/>).
    /// </summary>
    internal string Point { get; init; } = string.Empty;

    /// <summary>
    /// What a <c>function</c> or <c>entity.update</c> action resolves against; <see langword="null"/> refuses both, as a host
    /// that registers nothing would.
    /// </summary>
    internal ActionHost? Host { get; init; }
}

