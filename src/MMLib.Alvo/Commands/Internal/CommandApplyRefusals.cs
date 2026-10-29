using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The apply guard's sentences for a command a change would unbind (spec 2026-10-14 §4.2): a pointer to the removed entity
/// or to each removed field its access rule reads, then the binder's own sentence for every problem.
/// </summary>
internal static class CommandApplyRefusals
{
    private const string FixPrefix = "Remove the command and redeploy first, or keep the ";

    /// <summary>The refusals for <paramref name="command"/>, which binds the current schema and would not bind the proposed one.</summary>
    /// <param name="command">The command the change would unbind.</param>
    /// <param name="problems">Its binding problems against the proposed catalog.</param>
    /// <param name="current">The schema now serving.</param>
    /// <param name="proposed">The schema the change proposes.</param>
    /// <param name="compiler">The CEL compiler.</param>
    internal static DescriptorValidationError[] For(
        CommandRegistration command, IEnumerable<ExtensionProblem> problems, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        [.. Precise(command, current, proposed, compiler), .. problems.Select(problem => ExtensionApplyRefusals.Generic(problem, DescriptorValidationSeverity.Error))];

    private static DescriptorValidationError[] Precise(CommandRegistration command, SchemaModel current, SchemaModel proposed, ICelCompiler compiler)
    {
        if (command.Entity is not { } entity || Named(current, entity) is not { } before)
        {
            return [];
        }

        if (Named(proposed, entity) is not { } after)
        {
            return [EntityRemoved(command.Id, entity)];
        }

        return [.. FieldsRead(command.Access.Source, before, compiler)
            .Where(field => !after.Fields.Any(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal)))
            .Select(field => FieldRemoved(command.Id, entity, field))];
    }

    private static string[] FieldsRead(string access, EntitySchema before, ICelCompiler compiler)
    {
        var result = compiler.Compile(access, CelProfile.Condition, before);
        return result.IsSuccess ? [.. EventConditionReads.FieldsReadOrCompared(result.Expression!.Root).Distinct(StringComparer.Ordinal)] : [];
    }

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static DescriptorValidationError EntityRemoved(string id, string entity) => new(
        $"/entities/{entity}",
        $"Entity '{entity}' is bound by extension '{id}' ({CommandBinding.Kind}, in this host): its route reads a row of it, so without it the command could never be invoked.",
        FixPrefix + "entity.",
        DescriptorValidationSeverity.Error);

    private static DescriptorValidationError FieldRemoved(string id, string entity, string field) => new(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is read by the access rule of extension '{id}' ({CommandBinding.Kind}, in this host): without it the rule would no longer compile, and the command would refuse every caller.",
        FixPrefix + "field.",
        DescriptorValidationSeverity.Error);
}
