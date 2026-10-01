using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Warns where a rule or a before-hook condition compares <c>@user.id</c> with a ref to an entity other than
/// <c>users</c> (spec §9, D51): the ref holds that entity's id, the caller's id is a user id, and the two are never
/// equal — so the comparison is never true, and an owner clause built on it admits nobody.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pass of its own, and only ever a warning.</b> <see cref="PolicyCatalogBuilder"/> refuses a descriptor on any
/// entry in its error list, so a warning appended there would refuse a descriptor that is valid. This pass runs beside
/// it, emits <see cref="DescriptorValidationSeverity.Warning"/> and nothing else, and reads the compiled trees without
/// changing one: what the validator accepts, and every error it reports, do not move.
/// </para>
/// <para>
/// <b>After-hook conditions are not checked:</b> they cannot read <c>@user</c> at all. A <c>uuid</c> or a
/// <c>string</c> compared with <c>@user.id</c> is not warned either — a <c>uuid</c> that holds a user id
/// (<c>assigned_user_id</c>, <c>assigned_to</c>) is exactly the right owner column, and the compiler already refuses a
/// <c>string</c>. The row's own <c>id</c> compared with the caller is out of scope, recorded rather than guessed.
/// </para>
/// <para>
/// <b>Each source is compiled a second time</b>, through the same <see cref="ICelCompiler"/> and profile the catalog
/// uses: per apply, never per request. A source that does not compile draws nothing here; its error is already
/// reported. A before-hook condition that compiles and is then refused for its phase (<c>old.</c> in
/// <c>beforeCreate</c>) does draw one, and <c>DescriptorValidator</c> drops a warning at any pointer that already
/// carries an error. A comparison repeated in one source is warned once.
/// </para>
/// </remarks>
internal static class OwnerComparisonCheck
{
    private const string UsersEntity = "users";

    /// <summary>One warning per comparison of the caller with another entity's ref, each at its rule's or condition's pointer.</summary>
    /// <param name="descriptor">The parsed descriptor, whose rules and before-hook conditions are the sources.</param>
    /// <param name="schema">The mapped schema, whose fields say what a ref points at.</param>
    /// <param name="compiler">The compiler the catalog compiles the same sources with.</param>
    internal static IReadOnlyList<DescriptorValidationError> Warnings(AlvoDescriptor descriptor, SchemaModel schema, ICelCompiler compiler) =>
    [
        .. from entity in schema.Entities
           let declared = descriptor.Entities.GetValueOrDefault(entity.Name)
           where declared is not null
           from source in RuleSources(entity.Name, declared.Rules).Concat(ConditionSources(entity.Name, declared.Hooks))
           from warning in WarningsIn(entity, source, compiler)
           select warning,
    ];

    private static IEnumerable<CheckedSource> RuleSources(string entity, AccessRules? rules)
    {
        if (rules is null)
        {
            return [];
        }

        (string Operation, string? Source)[] operations =
            [("list", rules.List), ("get", rules.Get), ("create", rules.Create), ("update", rules.Update), ("delete", rules.Delete)];
        return operations
            .Where(operation => operation.Source is not null)
            .Select(operation => new CheckedSource(operation.Source!, CelProfile.Rule, $"/entities/{entity}/rules/{operation.Operation}"));
    }

    private static IEnumerable<CheckedSource> ConditionSources(string entity, EntityHooks? hooks) =>
        hooks is null
            ? []
            : Conditions(entity, "beforeCreate", hooks.BeforeCreate)
                .Concat(Conditions(entity, "beforeUpdate", hooks.BeforeUpdate))
                .Concat(Conditions(entity, "beforeDelete", hooks.BeforeDelete));

    private static IEnumerable<CheckedSource> Conditions(string entity, string point, IReadOnlyList<BeforeHook>? hooks) =>
        (hooks ?? [])
            .Select((hook, index) => (hook.Condition, Index: index))
            .Where(hook => hook.Condition is not null)
            .Select(hook => new CheckedSource(hook.Condition!, CelProfile.Condition, $"/entities/{entity}/hooks/{point}/{hook.Index}/condition"));

    private static IEnumerable<DescriptorValidationError> WarningsIn(EntitySchema entity, CheckedSource source, ICelCompiler compiler)
    {
        var result = compiler.Compile(source.Source, source.Profile, entity);
        if (!result.IsSuccess)
        {
            return [];
        }

        return Nodes(result.Expression!.Root)
            .Select(ComparedField)
            .OfType<string>()
            .Select(name => entity.Fields.FirstOrDefault(field => field.Name == name))
            .Where(field => field?.Reference is { } reference && !string.Equals(reference.TargetEntity, UsersEntity, StringComparison.Ordinal))
            .Select(field => Warning(source.Pointer, field!.Name, field.Reference!.TargetEntity))
            .Distinct();
    }

    /// <summary>The node and every node below it, read through <see cref="CelTree.Children"/> — the one walker shape.</summary>
    private static IEnumerable<CelNode> Nodes(CelNode node) => CelTree.Children(node).SelectMany(Nodes).Prepend(node);

    /// <summary>The field an <c>==</c> or <c>!=</c> compares with <c>@user.id</c>, in either order; else <see langword="null"/>.</summary>
    private static string? ComparedField(CelNode node) => node switch
    {
        CelBinary { Operator: CelBinaryOperator.Equal or CelBinaryOperator.NotEqual } binary => (binary.Left, binary.Right) switch
        {
            (CelContextRef { Value: CelContextValue.UserId }, CelFieldRef field) => field.FieldName,
            (CelFieldRef field, CelContextRef { Value: CelContextValue.UserId }) => field.FieldName,
            _ => null,
        },
        _ => null,
    };

    private static DescriptorValidationError Warning(string pointer, string field, string target) => new(
        pointer,
        $"'{field}' holds a {target} id, and @user.id is a user id — they are never equal, so this comparison is never true.",
        "Compare @user.id with a field of this entity that refs 'users', and add one (such as assigned_user_id) when there "
        + $"is none: a rule cannot follow a ref to '{target}' to its user.",
        DescriptorValidationSeverity.Warning);

    /// <summary>One source the pass compiles: its text, its profile, and the pointer a warning about it lands at.</summary>
    private sealed record CheckedSource(string Source, CelProfile Profile, string Pointer);
}
