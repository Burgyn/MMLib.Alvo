using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The entities block: adding, renaming and removing an entity — and the rename rules RenameField reuses one
   level down. */
internal sealed partial class WorkingCopy
{
    /// <summary>The entity names the working document declares.</summary>
    public IReadOnlyList<string> Entities => Read<IReadOnlyList<string>>(
        () => _working?["entities"] is JsonObject entities ? [.. entities.Select(entity => entity.Key)] : []);

    /// <summary>Adds an entity with one required string field, the smallest thing the schema admits.</summary>
    /// <param name="name">The entity's name.</param>
    /// <param name="scoped">Whether it is tenant-scoped.</param>
    /// <param name="audited">Whether it carries the audit columns.</param>
    public void AddEntity(string name, bool scoped, bool audited) => Edit(root =>
    {
        Ensure(root, "entities")[name] = new JsonObject
        {
            ["tenancy"] = scoped ? "scoped" : "global",
            ["audit"] = audited,
            ["fields"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["required"] = true,
                    ["maxLength"] = 120,
                },
            },
        };

        return true;
    });

    /// <summary>
    /// Renames an entity, carrying the rename so the apply moves the table instead of dropping it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key moves and <c>renamedFrom</c> records where it came from</b>, because those are one
    /// operation and not two. Renaming the key alone is a drop and a create to the apply — the old table
    /// goes, with its rows — which is the data loss the schema has this key to prevent.
    /// </para>
    /// <para>
    /// <b><c>renamedFrom</c> always names the APPLIED name, never an intermediate.</b> An operator who
    /// renames <c>jobs</c> to <c>work_orders</c> and then to <c>orders</c> has one applied table called
    /// <c>jobs</c>, so that is what the apply has to be told; recording <c>work_orders</c> would point it at
    /// a table that never existed. And a rename back to the applied name drops the key entirely, because
    /// then nothing moved.
    /// </para>
    /// <para>
    /// <b>An entity this copy invented carries no <c>renamedFrom</c> at all.</b> It has no table behind it,
    /// so there is nothing to move and the key would name something the apply cannot find.
    /// </para>
    /// </remarks>
    /// <param name="from">The name it has now.</param>
    /// <param name="to">The name it should have.</param>
    /// <returns>A sentence saying why it cannot, or <see langword="null"/> when it was renamed.</returns>
    public string? RenameEntity(string from, string to)
    {
        string? refusal = null;
        Edit(root =>
        {
            if (root["entities"] is not JsonObject entities || entities[from] is not JsonObject declared)
            {
                refusal = $"There is no entity called {from} in this working copy.";
                return false;
            }

            refusal = Refusal(from, to, entities, "entity");
            if (refusal is not null)
            {
                return false;
            }

            var origin = Origin(declared, from, _applied?["entities"]?[from] is not null);
            Rekey(entities, from, to);
            Carry(entities[to] as JsonObject, origin, to);
            return true;
        });

        return refusal;
    }

    /// <summary>Removes an entity.</summary>
    public void RemoveEntity(string name) => Edit(root => (root["entities"] as JsonObject)?.Remove(name) is true);

    /// <summary>The name this entity is applied under, which is what a field's origin has to be read against.</summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    private string AppliedNameOf(string entity) =>
        _working?["entities"]?[entity]?["renamedFrom"]?.GetValue<string>() ?? entity;

    /// <summary>
    /// The applied name this declaration came from, or <see langword="null"/> when it never had one.
    /// </summary>
    /// <param name="declared">The declaration being renamed.</param>
    /// <param name="from">The name it carries in the working copy right now.</param>
    /// <param name="isApplied">Whether <paramref name="from"/> is a name the applied document knows.</param>
    private static string? Origin(JsonObject declared, string from, bool isApplied)
        => declared["renamedFrom"] is JsonValue existing && existing.TryGetValue<string>(out var origin)
            ? origin
            : isApplied ? from : null;

    /// <summary>Writes, or drops, the rename the apply reads.</summary>
    /// <param name="declared">The renamed declaration.</param>
    /// <param name="origin">The applied name it came from, or <see langword="null"/>.</param>
    /// <param name="to">The name it now has.</param>
    private static void Carry(JsonObject? declared, string? origin, string to)
    {
        if (declared is null)
        {
            return;
        }

        if (origin is null || string.Equals(origin, to, StringComparison.Ordinal))
        {
            declared.Remove("renamedFrom");
            return;
        }

        declared["renamedFrom"] = origin;
    }

    /// <summary>Why this rename cannot happen, or <see langword="null"/>.</summary>
    /// <param name="from">The current name.</param>
    /// <param name="to">The proposed name.</param>
    /// <param name="owner">The object the name is a key of.</param>
    /// <param name="what">The word for what is being renamed, for the sentence.</param>
    private static string? Refusal(string from, string to, JsonObject owner, string what)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return "That is the name it already has.";
        }

        if (!DescriptorNames.IsMember(to))
        {
            return $"A {what} name must match {DescriptorNames.Member}.";
        }

        return owner[to] is not null
            ? $"This project already declares a {what} called {to}."
            : null;
    }

    /// <summary>
    /// Moves a key without moving its position.
    /// </summary>
    /// <remarks>
    /// Removing and re-adding would send the renamed declaration to the end of the object, so a rename
    /// would land in the diff as the whole block moving. The descriptor is a file somebody reads and
    /// commits, and a one-word change should read as one word.
    /// </remarks>
    /// <param name="owner">The object to rewrite.</param>
    /// <param name="from">The key to replace.</param>
    /// <param name="to">The key to replace it with.</param>
    private static void Rekey(JsonObject owner, string from, string to)
    {
        var order = owner
            .Select(pair => (
                Key: string.Equals(pair.Key, from, StringComparison.Ordinal) ? to : pair.Key,
                Value: pair.Value?.DeepClone()))
            .ToList();

        owner.Clear();
        foreach (var (key, value) in order)
        {
            owner[key] = value;
        }
    }
}
