using System.Text.Json;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Reads one part of a descriptor without projecting the whole of it onto types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lens and not a model.</b> Design §6.3 criterion 3: everything clickable is exportable
/// as code, and after any UI change <c>GET descriptor</c> must equal what the editor sent. A
/// projection onto typed objects breaks that the first time the schema grows a key the projection
/// does not know about — the round trip silently drops it. So the descriptor is carried as JSON
/// end to end, and a screen that needs one branch of it reads that branch.
/// </para>
/// <para>
/// Every accessor answers with a missing value rather than throwing. A descriptor this dashboard
/// did not write is still a descriptor the apply accepted, and a screen that threw on an absent
/// optional block would be refusing to render valid configuration.
/// </para>
/// </remarks>
internal static class DescriptorLens
{
    /// <summary>The rules declared for one entity, by operation.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="entity">The entity to read.</param>
    /// <returns>Operation to CEL source, in the descriptor's own order.</returns>
    public static IReadOnlyList<KeyValuePair<string, string>> Rules(string descriptorJson, string entity)
        => Pairs(descriptorJson, entity, "rules");

    /// <summary>The hooks declared for one entity, by point.</summary>
    /// <remarks>
    /// The value is the raw JSON of the hook list rather than a sentence: a before-hook and an
    /// after-hook admit different action shapes (<c>$defs/beforeHookList</c> takes <c>reject</c>
    /// and <c>mutate</c> only — <i>"no network, no external calls"</i>), and a renderer that
    /// flattened both into one wording would be inventing a third.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="entity">The entity to read.</param>
    /// <returns>Hook point to the raw JSON of its action list.</returns>
    public static IReadOnlyList<KeyValuePair<string, string>> Hooks(string descriptorJson, string entity)
        => Pairs(descriptorJson, entity, "hooks");

    /// <summary>How many rules the descriptor declares, over every entity and operation.</summary>
    /// <remarks>
    /// One parse for the whole count, because Overview shows it beside a link and a count that parsed
    /// the document once per entity would cost more than the screen it decorates.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    public static int RuleCount(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document is null
            || !document.RootElement.TryGetProperty("entities", out var entities)
            || entities.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return entities.EnumerateObject()
            .Where(entity => entity.Value.ValueKind == JsonValueKind.Object)
            .Select(entity => entity.Value.TryGetProperty("rules", out var rules)
                && rules.ValueKind == JsonValueKind.Object
                    ? rules.EnumerateObject().Count()
                    : 0)
            .Sum();
    }

    /// <summary>The top-level blocks this descriptor declares.</summary>
    public static IReadOnlySet<string> DeclaredBlocks(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document?.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The role names <c>auth.roles</c> declares.</summary>
    public static IReadOnlyList<string> DeclaredRoles(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document is null
            || !document.RootElement.TryGetProperty("auth", out var auth)
            || !auth.TryGetProperty("roles", out var roles)
            || roles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return roles.EnumerateArray()
            .Where(role => role.ValueKind == JsonValueKind.String)
            .Select(role => role.GetString()!)
            .ToList();
    }

    /// <summary>The three management levels and the CEL that decides each.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> AccessLevels(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document is null
            || !document.RootElement.TryGetProperty("access", out var access)
            || access.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. access.EnumerateObject()
            .Where(level => level.Value.ValueKind == JsonValueKind.String)
            .Select(level => new KeyValuePair<string, string>(level.Name, level.Value.GetString()!))];
    }

    /// <summary>One top-level block's raw JSON, when it is declared.</summary>
    public static string? Block(string descriptorJson, string block)
    {
        using var document = Parse(descriptorJson);
        return document is not null && document.RootElement.TryGetProperty(block, out var value)
            ? value.GetRawText()
            : null;
    }

    /// <summary>The fields one entity declares <c>hidden</c>.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="entity">The entity to read.</param>
    public static FieldMasks Masks(string descriptorJson, string entity)
    {
        var (always, conditional) = BoolOrCel(descriptorJson, entity, "hidden");
        return always.Count + conditional.Count == 0 ? FieldMasks.None : new FieldMasks(always, conditional);
    }

    /// <summary>The fields one entity declares <c>readOnly</c>.</summary>
    /// <remarks>
    /// Like <c>hidden</c>, a policy the resolved schema does not carry, and split the same way: <c>true</c>
    /// freezes the field for every caller, a CEL expression for some.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="entity">The entity to read.</param>
    public static FieldLocks Locks(string descriptorJson, string entity)
    {
        var (always, conditional) = BoolOrCel(descriptorJson, entity, "readOnly");
        return always.Count + conditional.Count == 0 ? FieldLocks.None : new FieldLocks(always, conditional);
    }

    /// <summary>The fields whose <paramref name="key"/> is <c>true</c>, and those whose is a CEL expression.</summary>
    private static (HashSet<string> Always, HashSet<string> Conditional) BoolOrCel(
        string descriptorJson, string entity, string key)
    {
        var always = new HashSet<string>(StringComparer.Ordinal);
        var conditional = new HashSet<string>(StringComparer.Ordinal);

        using var document = Parse(descriptorJson);
        if (document is null
            || !document.RootElement.TryGetProperty("entities", out var entities)
            || !entities.TryGetProperty(entity, out var declared)
            || !declared.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Object)
        {
            return (always, conditional);
        }

        foreach (var field in fields.EnumerateObject())
        {
            var kind = KindOf(field.Value, key);
            if (kind == JsonValueKind.True)
            {
                always.Add(field.Name);
            }
            else if (kind == JsonValueKind.String)
            {
                conditional.Add(field.Name);
            }
        }

        return (always, conditional);
    }

    private static JsonValueKind KindOf(JsonElement field, string key)
        => field.ValueKind == JsonValueKind.Object && field.TryGetProperty(key, out var value)
            ? value.ValueKind
            : JsonValueKind.Undefined;

    private static IReadOnlyList<KeyValuePair<string, string>> Pairs(
        string descriptorJson, string entity, string block)
    {
        using var document = Parse(descriptorJson);
        if (document is null
            || !document.RootElement.TryGetProperty("entities", out var entities)
            || !entities.TryGetProperty(entity, out var declared)
            || !declared.TryGetProperty(block, out var target)
            || target.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. target.EnumerateObject().Select(property => new KeyValuePair<string, string>(
            property.Name,
            property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : property.Value.GetRawText()))];
    }

    /// <summary>
    /// Parses, or answers nothing.
    /// </summary>
    /// <remarks>
    /// The stored descriptor went through the apply, so it parses — but this same lens reads a
    /// descriptor an operator has just pasted into the import box, and that one has not. A throw
    /// there would blank the screen that was about to explain the problem.
    /// </remarks>
    private static JsonDocument? Parse(string descriptorJson)
    {
        try
        {
            return JsonDocument.Parse(descriptorJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
