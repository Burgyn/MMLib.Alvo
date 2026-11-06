using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Warns where a rule reads a relation into an entity with no <c>list</c> rule: no caller may list that entity's rows,
/// so the relation never matches one, and the clause holding it is never true.
/// </summary>
/// <remarks>
/// A pass of its own, beside <see cref="PolicyCatalogBuilder"/>, whose error list would refuse a valid descriptor; it
/// emits <see cref="DescriptorValidationSeverity.Warning"/> only, the <see cref="OwnerComparisonCheck"/> shape. Each
/// rule is compiled a second time with the same relation catalog, per apply; a rule that does not compile draws
/// nothing, since its error is already reported.
/// </remarks>
internal static class RelationWarnings
{
    /// <summary>One warning per related entity with no list rule, at the pointer of each rule that reads it.</summary>
    /// <param name="descriptor">The parsed descriptor, whose rules are the sources.</param>
    /// <param name="schema">The mapped schema the rules compile against.</param>
    /// <param name="compiler">The compiler the catalog compiles the same rules with.</param>
    internal static IReadOnlyList<DescriptorValidationError> Warnings(AlvoDescriptor descriptor, SchemaModel schema, ICelCompiler compiler)
    {
        var relations = RelationCatalogBuilder.From(descriptor, schema);
        return
        [
            .. from entity in schema.Entities
               let rules = descriptor.Entities.GetValueOrDefault(entity.Name)?.Rules
               where rules is not null
               from rule in Sources(rules)
               let compiled = compiler.Compile(rule.Source, CelProfile.Rule, entity, relations)
               where compiled.IsSuccess
               from related in RelationGraph.Targets(compiled.Expression!.Root)
               where descriptor.Entities.GetValueOrDefault(related)?.Rules?.List is null
               select Warning($"/entities/{entity.Name}/rules/{rule.Operation}", related),
        ];
    }

    private static IEnumerable<(string Operation, string Source)> Sources(AccessRules rules) =>
        new (string Operation, string? Source)[]
        {
            ("list", rules.List), ("get", rules.Get), ("create", rules.Create), ("update", rules.Update), ("delete", rules.Delete),
        }
        .Where(rule => rule.Source is not null)
        .Select(rule => (rule.Operation, rule.Source!));

    private static DescriptorValidationError Warning(string pointer, string related) => new(
        pointer,
        $"'{related}' has no list rule, so this relation never matches any row.",
        $"Give '{related}' a list rule.",
        DescriptorValidationSeverity.Warning);
}
