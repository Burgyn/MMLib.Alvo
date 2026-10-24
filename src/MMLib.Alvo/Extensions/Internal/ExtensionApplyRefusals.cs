using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Turns binding problems into descriptor findings. A refusal names, for a handler the change unbinds, the entity, the
/// field or the <c>events.exclude</c> item the change removed or excluded, or the field whose new type the condition no
/// longer compiles against, or that its typed record maps and could no longer fill — each existed before the change,
/// since the handler bound it — and keeps the compiler's own
/// sentence beside it, so a second cause is not hidden behind the first. A warning is the compiler's sentence alone,
/// for a handler that bound neither catalog.
/// </summary>
internal static class ExtensionApplyRefusals
{
    private const string Kind = ExtensionBindingCompiler.HandlerKind;
    private const string FixPrefix = "Remove the registration and redeploy first, or keep the ";

    internal static IReadOnlyList<DescriptorValidationError> Refusals(
        ExtensionRegistry registry, IReadOnlyList<ExtensionProblem> regressions, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        [.. regressions
            .GroupBy(problem => problem.Id, StringComparer.Ordinal)
            .SelectMany(refused => ForHandler(Registered(registry, refused.Key), refused, current, proposed, compiler))];

    internal static IReadOnlyList<DescriptorValidationError> Warnings(IReadOnlyList<ExtensionProblem> problems) =>
        [.. problems.Select(problem => Generic(problem, DescriptorValidationSeverity.Warning))];

    private static DescriptorValidationError[] ForHandler(
        HandlerRegistration handler, IEnumerable<ExtensionProblem> problems, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        [.. Precise(handler, current, proposed, compiler), .. problems.Select(problem => Generic(problem, DescriptorValidationSeverity.Error))];

    private static HandlerRegistration Registered(ExtensionRegistry registry, string id) =>
        registry.Handlers.First(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));

    private static DescriptorValidationError[] Precise(
        HandlerRegistration handler, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        handler.Source is EntityEvents { IsAll: false } source
            ? [.. source.Entities.SelectMany(entity => ForEntity(handler, entity, current, proposed, compiler))]
            : [];

    private static DescriptorValidationError[] ForEntity(
        HandlerRegistration handler, string name, SchemaModel current, SchemaModel proposed, ICelCompiler compiler)
    {
        if (Named(proposed, name) is not { } after)
        {
            return [EntityRemoved(handler.Id, name)];
        }

        if (Named(current, name) is not { } before)
        {
            return [];
        }

        var read = FieldsRead(handler.When, before, compiler)
            .Select(field => (Field: field, Refusal: FieldRefusal(handler, before, after, field, compiler)))
            .Where(entry => entry.Refusal is not null)
            .ToDictionary(entry => entry.Field, entry => entry.Refusal!, StringComparer.Ordinal);

        return [.. read.Values, .. Mapped(handler, read.Keys).Select(mapped => MappedRefusal(handler.Id, mapped.Shape, mapped.Property, after)).OfType<DescriptorValidationError>()];
    }

    /// <summary>
    /// The fields a typed record maps that the condition's refusals do not already point at: a field the change breaks for
    /// both gets the condition's sentence once, and one whose change the condition survives still gets the record's.
    /// </summary>
    private static IEnumerable<(RecordShape Shape, MappedProperty Property)> Mapped(HandlerRegistration handler, IEnumerable<string> refused) =>
        handler.Typed is { } typed
            ? typed.Shape.Properties.Where(property => !refused.Contains(property.Field, StringComparer.Ordinal)).Select(property => (typed.Shape, property))
            : [];

    private static DescriptorValidationError? MappedRefusal(string id, RecordShape shape, MappedProperty property, EntitySchema after)
    {
        if (Field(after, property.Field) is not { } now)
        {
            return MappedRemoved(id, after.Name, property.Field, shape.Name);
        }

        var index = after.EventImageExclusions.ToList().IndexOf(property.Field);
        if (index >= 0)
        {
            return MappedExcluded(id, after.Name, property.Field, shape.Name, index);
        }

        return ShapeMapping.Holds(property, now) ? MappedNarrowed(id, shape, property, now, after.Name) : MappedRetyped(id, after.Name, property.Field, shape.Name);
    }

    /// <summary>A field that still holds the property's type, but now may be empty where the property cannot, or lacks a value its enum maps.</summary>
    private static DescriptorValidationError? MappedNarrowed(string id, RecordShape shape, MappedProperty property, FieldSchema now, string entity)
    {
        if (now.Nullable && !property.AcceptsNull)
        {
            return MappedMadeOptional(id, entity, now.Name, $"{shape.Name}.{property.Property.Name}");
        }

        var declared = now.EnumValues ?? [];
        var missing = property.Enum?.Members.Where(member => !declared.Contains(member.Value, StringComparer.Ordinal)).ToList() ?? [];

        return missing.Count == 0 ? null : MappedValueRemoved(id, entity, now.Name, shape.Name, missing[0]);
    }

    private static DescriptorValidationError? FieldRefusal(
        HandlerRegistration handler, EntitySchema before, EntitySchema after, string field, ICelCompiler compiler)
    {
        if (Field(after, field) is not { } now)
        {
            return FieldRemoved(handler.Id, after.Name, field);
        }

        var index = after.EventImageExclusions.ToList().IndexOf(field);
        if (index >= 0)
        {
            return FieldExcluded(handler.Id, after.Name, field, index);
        }

        return BreaksCondition(handler.When!, before, now, compiler) ? FieldRetyped(handler.Id, after.Name, field) : null;
    }

    private static bool BreaksCondition(string when, EntitySchema before, FieldSchema now, ICelCompiler compiler)
    {
        if (Field(before, now.Name) is not { } was || was.Type == now.Type)
        {
            return false;
        }

        var onlyThisRetyped = before with { Fields = [.. before.Fields.Select(field => field == was ? now : field)] };
        return !compiler.Compile(when, CelProfile.Condition, onlyThisRetyped).IsSuccess;
    }

    private static FieldSchema? Field(EntitySchema entity, string name) =>
        entity.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

    private static string[] FieldsRead(string? when, EntitySchema before, ICelCompiler compiler)
    {
        if (when is null)
        {
            return [];
        }

        var result = compiler.Compile(when, CelProfile.Condition, before);
        return result.IsSuccess
            ? [.. EventConditionReads.FieldsReadOrCompared(result.Expression!.Root).Distinct(StringComparer.Ordinal)]
            : [];
    }

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static DescriptorValidationError EntityRemoved(string id, string entity) => Error(
        $"/entities/{entity}",
        $"Entity '{entity}' is bound by extension '{id}' ({Kind}, in this host): without it the handler would run on "
        + "nothing, and every event it would have handled would be recorded dead.",
        FixPrefix + "entity.");

    private static DescriptorValidationError FieldRemoved(string id, string entity, string field) => Error(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is bound by extension '{id}' ({Kind}, in this host): its condition reads it, so "
        + "without it the handler would no longer bind, and every event it would have handled would be recorded dead.",
        FixPrefix + "field.");

    private static DescriptorValidationError FieldExcluded(string id, string entity, string field, int index) => Error(
        $"/entities/{entity}/events/exclude/{index}",
        $"Field '{field}' of '{entity}' is read by extension '{id}' ({Kind}, in this host): left out of the event images, "
        + "it would decide the handler's condition by its absence.",
        FixPrefix + "field in the event images.");

    private static DescriptorValidationError FieldRetyped(string id, string entity, string field) => Error(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is read by extension '{id}' ({Kind}, in this host): with its new type the "
        + "condition no longer compiles, so the handler would no longer bind, and every event it would have handled "
        + "would be recorded dead.",
        FixPrefix + "field's type.");

    private static DescriptorValidationError MappedRemoved(string id, string entity, string field, string type) => Error(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is bound by extension '{id}' ({Kind}, in this host): its typed record {type} maps "
        + "it, so without it the handler would no longer bind, and every event it would have handled would be recorded dead.",
        FixPrefix + "field.");

    private static DescriptorValidationError MappedExcluded(string id, string entity, string field, string type, int index) => Error(
        $"/entities/{entity}/events/exclude/{index}",
        $"Field '{field}' of '{entity}' is read by extension '{id}' ({Kind}, in this host): its typed record {type} maps "
        + "it, so left out of the event images it could never fill the record, and the handler would no longer bind.",
        FixPrefix + "field in the event images.");

    private static DescriptorValidationError MappedRetyped(string id, string entity, string field, string type) => Error(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is read by extension '{id}' ({Kind}, in this host): its typed record {type} maps "
        + "it and cannot hold its new type, so the handler would no longer bind, and every event it would have handled "
        + "would be recorded dead.",
        FixPrefix + "field's type.");

    private static DescriptorValidationError MappedMadeOptional(string id, string entity, string field, string property) => Error(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is read by extension '{id}' ({Kind}, in this host): {property} maps it and cannot "
        + "hold null, so with the field optional the handler would no longer bind, and every event it would have handled "
        + "would be recorded dead.",
        FixPrefix + "field required.");

    private static DescriptorValidationError MappedValueRemoved(string id, string entity, string field, string type, EnumMember member) => Error(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is read by extension '{id}' ({Kind}, in this host): its typed record {type} maps "
        + $"it, and {member.Member.GetType().Name}.{member.Name} maps to '{member.Value}', which the field would no longer "
        + "declare, so the handler would no longer bind, and every event it would have handled would be recorded dead.",
        FixPrefix + $"value '{member.Value}'.");

    private static DescriptorValidationError Generic(ExtensionProblem problem, DescriptorValidationSeverity severity) => new(
        string.Empty,
        severity == DescriptorValidationSeverity.Error
            ? $"Extension '{problem.Id}' ({problem.Kind}, in this host) would no longer bind to this descriptor — {problem.Locator}: {problem.Message}"
            : $"Extension '{problem.Id}' ({problem.Kind}, in this host) does not bind to this descriptor, and did not before this change — {problem.Locator}: {problem.Message}",
        problem.Fix,
        severity);

    private static DescriptorValidationError Error(string path, string message, string fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}
