using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Internal;

/* An entity's fields: adding, renaming, removing and restoring one, and the two readings the Fields tab draws. */
internal sealed partial class WorkingCopy
{
    /// <summary>
    /// Renames a field, carrying the rename so the apply moves the column instead of dropping it.
    /// </summary>
    /// <remarks>
    /// <see cref="RenameEntity"/>'s rules, one level down — including that the origin is read against the
    /// applied document, so a field on an entity this copy invented carries no <c>renamedFrom</c> either.
    /// </remarks>
    /// <param name="entity">The entity the field is on.</param>
    /// <param name="from">The name it has now.</param>
    /// <param name="to">The name it should have.</param>
    /// <returns>A sentence saying why it cannot, or <see langword="null"/> when it was renamed.</returns>
    public string? RenameField(string entity, string from, string to)
    {
        string? refusal = null;
        Edit(root =>
        {
            if (root["entities"]?[entity]?["fields"] is not JsonObject fields
                || fields[from] is not JsonObject declared)
            {
                refusal = $"There is no field called {from} on {entity}.";
                return false;
            }

            refusal = Refusal(from, to, fields, "field");
            if (refusal is not null)
            {
                return false;
            }

            /* Against the entity's applied name, not its working one: renaming a field on an entity that was
               itself renamed in this copy must still resolve the column that exists in the database. */
            var appliedEntity = AppliedNameOf(entity);
            var origin = Origin(
                declared, from, _applied?["entities"]?[appliedEntity]?["fields"]?[from] is not null);

            Rekey(fields, from, to);
            Carry(fields[to] as JsonObject, origin, to);
            return true;
        });

        return refusal;
    }

    /// <summary>Adds a field to an entity.</summary>
    /// <param name="entity">The entity to add it to.</param>
    /// <param name="name">The field's name.</param>
    /// <param name="facets">Its type and facets, already in the schema's own shape.</param>
    public void AddField(string entity, string name, JsonObject facets) => Edit(root =>
    {
        if (root["entities"]?[entity] is not JsonObject declared)
        {
            return false;
        }

        Ensure(declared, "fields")[name] = facets;
        return true;
    });

    /// <summary>Removes a field from an entity.</summary>
    public void RemoveField(string entity, string name)
        => Edit(root => (root["entities"]?[entity]?["fields"] as JsonObject)?.Remove(name) is true);

    /// <summary>
    /// Puts a removed field back, exactly as the applied revision declares it.
    /// </summary>
    /// <remarks>
    /// <b>Back in its place, not at the end.</b> <see cref="IsDirty"/> compares the documents as text, so a
    /// field restored to the end of the object would leave the copy dirty with nothing an operator could
    /// name as a change — undoing the only edit must leave nothing to preview.
    /// </remarks>
    /// <param name="entity">The entity's name in the working copy.</param>
    /// <param name="name">The field's applied name.</param>
    public void RestoreField(string entity, string name) => Edit(_ =>
    {
        if (AppliedEntityOf(entity)?["fields"] is not JsonObject applied
            || applied[name] is not { } declared
            || WorkingEntityOf(entity) is not { } working)
        {
            return false;
        }

        var fields = Ensure(working, "fields");
        if (fields.ContainsKey(name))
        {
            return false;
        }

        Reinsert(fields, name, declared.DeepClone(), applied);
        return true;
    });

    /// <summary>The fields an entity declares in the working copy, with their raw JSON.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> FieldsOf(string entity)
        => Read<IReadOnlyList<KeyValuePair<string, string>>>(
            () => _working?["entities"]?[entity]?["fields"] is JsonObject fields
                ? [.. fields.Select(pair => new KeyValuePair<string, string>(
                    pair.Key, Readable(pair.Value, "{}")))]
                : []);

    /// <summary>
    /// An entity's fields as the Fields tab lists them: the working copy's, and the ones it removed.
    /// </summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    public IReadOnlyList<StagedField> FieldChangesOf(string entity)
        => Read(() => StagedChanges.Fields(AppliedEntityOf(entity), WorkingEntityOf(entity)));

    /// <summary>Inserts a key just after the last working key whose applied name preceded it.</summary>
    /// <remarks>
    /// Rebuilt rather than appended, for the reason <see cref="Rekey"/> gives. A working key is read through
    /// its <c>renamedFrom</c>, as <c>StagedChanges</c> places a removed row: a field renamed since it was
    /// applied still stands where its applied name stood.
    /// </remarks>
    private static void Reinsert(JsonObject owner, string name, JsonNode value, JsonObject applied)
    {
        var order = applied.Select(pair => pair.Key).ToList();
        var earlier = order.Take(order.IndexOf(name)).ToHashSet(StringComparer.Ordinal);
        var pairs = owner.Select(pair => (pair.Key, Value: pair.Value?.DeepClone())).ToList();
        var at = pairs.FindLastIndex(
            pair => StagedChanges.OriginOf(pair.Value, pair.Key, applied) is { } origin && earlier.Contains(origin)) + 1;

        pairs.Insert(at, (name, value));
        owner.Clear();
        foreach (var (key, node) in pairs)
        {
            owner[key] = node;
        }
    }
}
