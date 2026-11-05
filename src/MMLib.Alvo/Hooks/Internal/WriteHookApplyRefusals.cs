using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// An apply refusal for a write hook the change would unbind (HX-04 (6)'s guard): a pointer at the removed entity or at each
/// field its condition read that is gone, with §4.2's fix, then the compiler's own sentence for every problem.
/// </summary>
internal static class WriteHookApplyRefusals
{
    private const string FixPrefix = "Remove the registration and redeploy first, or keep the ";

    internal static IReadOnlyList<DescriptorValidationError> For(
        WriteHookRegistration hook, IEnumerable<ExtensionProblem> problems, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        [.. hook.IsEveryEntity ? [] : hook.Entities.SelectMany(entity => Precise(hook, entity, current, proposed, compiler)),
         .. problems.Select(problem => ExtensionApplyRefusals.Generic(problem, DescriptorValidationSeverity.Error))];

    private static IEnumerable<DescriptorValidationError> Precise(
        WriteHookRegistration hook, string name, SchemaModel current, SchemaModel proposed, ICelCompiler compiler)
    {
        var kind = WriteHookBinding.KindOf(hook.Stage);
        if (Named(proposed, name) is not { } after)
        {
            return [Error($"/entities/{name}", $"Entity '{name}' is bound by extension '{hook.Id}' ({kind}, in this host): without it every write to it would be refused as unbound.", FixPrefix + "entity.")];
        }

        return Named(current, name) is { } before && hook.When is { } when
            ? FieldsRead(when, before, compiler).Where(field => !after.Fields.Any(kept => kept.Name == field)).Select(field => Error(
                $"/entities/{name}/fields/{field}",
                $"Field '{field}' of '{name}' is read by extension '{hook.Id}' ({kind}, in this host): without it the hook would no longer bind, and every write to '{name}' would be refused as unbound.",
                FixPrefix + "field."))
            : [];
    }

    private static string[] FieldsRead(string when, EntitySchema entity, ICelCompiler compiler)
    {
        var result = compiler.Compile(when, CelProfile.Condition, entity);
        return result.IsSuccess ? [.. EventConditionReads.FieldsReadOrCompared(result.Expression!.Root).Distinct(StringComparer.Ordinal)] : [];
    }

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static DescriptorValidationError Error(string path, string message, string fix) => new(path, message, fix, DescriptorValidationSeverity.Error);
}
