namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The validator's pass over every <c>virtual</c> field: a field with no column, so every key that would store,
/// constrain, index or reference its value is refused beside it — one finding per field, at the pointer that fixes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the order a reader fixes them in.</b> The field's own keys first (<c>computed</c>, <c>rollup</c>,
/// <c>default</c>, <c>required</c>, <c>unique</c>, <c>index</c>, <c>validation</c>, <c>renamedFrom</c>, then a
/// <c>ref</c> type reported as its <c>entity</c> key), then a node elsewhere naming it (an index, another entity's
/// rollup, the entity's event exclusions), then the dynamic-entity form. Only the first applies, because each is the
/// same defect — the field cannot be both virtual and stored — and one fix answers all of them.
/// </para>
/// <para>
/// <b>A CEL expression reading one is not refused here.</b> Every slot compiles against the entity schema, so the type
/// checker refuses it at the expression's own pointer, in every profile.
/// </para>
/// </remarks>
internal static class VirtualFieldCheck
{
    private const string NoColumn = "a virtual field has no column, so nothing stores, constrains, indexes or references it.";

    private static readonly (string Key, Func<FieldDescriptor, bool> Declares)[] _storageKeys =
    [
        ("computed", field => field.Computed is not null),
        ("rollup", field => field.Rollup is not null),
        ("default", field => field.Default is not null),
        ("required", field => field.Required == true),
        ("unique", field => field.Unique == true),
        ("index", field => field.Index == true),
        ("validation", field => field.Validation is not null),
        ("renamedFrom", field => field.RenamedFrom is not null),
        ("entity", field => field.Type == FieldType.Ref),
    ];

    /// <summary>Every refusal over <paramref name="descriptor"/>'s virtual fields, at most one per field.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IEnumerable<DescriptorValidationError> Errors(AlvoDescriptor descriptor) =>
        from entity in descriptor.Entities
        from field in entity.Value.Fields
        where field.Value.Virtual == true
        let error = FirstRefusal(descriptor, entity.Key, field.Key)
        where error is not null
        select error;

    private static DescriptorValidationError? FirstRefusal(AlvoDescriptor descriptor, string entity, string field) =>
        StorageKeyRefusal(entity, field, descriptor.Entities[entity].Fields[field])
        ?? ReferencePaths(descriptor, entity, field).Select(path => Referenced(path, entity, field)).FirstOrDefault()
        ?? DynamicRefusal(entity, field, descriptor.Entities[entity]);

    private static DescriptorValidationError? StorageKeyRefusal(string entity, string field, FieldDescriptor declared) =>
        _storageKeys.FirstOrDefault(key => key.Declares(declared)).Key is { } key
            ? Error(
                $"/entities/{entity}/fields/{field}/virtual",
                $"Field '{entity}.{field}' is virtual and declares '{key}': {NoColumn}",
                $"Remove '{key}', or drop 'virtual' to store the field.")
            : null;

    private static IEnumerable<string> ReferencePaths(AlvoDescriptor descriptor, string entity, string field) =>
        [
            .. IndexPaths(descriptor.Entities[entity], entity, field),
            .. RollupPaths(descriptor, entity, field),
            .. ExcludePaths(descriptor.Entities[entity], entity, field),
        ];

    private static IEnumerable<string> IndexPaths(EntityDescriptor declaring, string entity, string field) =>
        (declaring.Indexes ?? [])
            .Select((index, position) => (index, position))
            .Where(pair => pair.index.Fields.Contains(field, StringComparer.Ordinal))
            .Select(pair => $"/entities/{entity}/indexes/{pair.position}/fields");

    private static IEnumerable<string> RollupPaths(AlvoDescriptor descriptor, string child, string field) =>
        from parent in descriptor.Entities
        from rollup in parent.Value.Fields
        where rollup.Value.Rollup is { } over && string.Equals(over.From, child, StringComparison.Ordinal)
        from key in RollupKeysNaming(rollup.Value.Rollup!, field)
        select $"/entities/{parent.Key}/fields/{rollup.Key}/rollup/{key}";

    private static IEnumerable<string> RollupKeysNaming(Rollup rollup, string field) =>
        new[] { (Key: "field", Named: rollup.Field), (Key: "via", Named: rollup.Via) }
            .Where(pair => string.Equals(pair.Named, field, StringComparison.Ordinal))
            .Select(pair => pair.Key);

    private static IEnumerable<string> ExcludePaths(EntityDescriptor declaring, string entity, string field) =>
        declaring.Events?.Exclude?.Contains(field, StringComparer.Ordinal) == true
            ? [$"/entities/{entity}/events/exclude"]
            : [];

    private static DescriptorValidationError Referenced(string path, string entity, string field) =>
        Error(path, $"Field '{entity}.{field}' is virtual: {NoColumn}", $"Remove '{field}' here, or drop 'virtual' to store the field.");

    private static DescriptorValidationError? DynamicRefusal(string entity, string field, EntityDescriptor declaring) =>
        declaring.Storage == StorageMode.Dynamic
            ? Error(
                $"/entities/{entity}/fields/{field}/virtual",
                $"Field '{entity}.{field}' is virtual and '{entity}' declares 'storage: dynamic' — dynamic entities hold no virtual field until #41.",
                $"Drop 'virtual', or keep '{entity}' physical.")
            : null;

    private static DescriptorValidationError Error(string path, string message, string fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}
