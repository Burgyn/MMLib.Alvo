using MMLib.Alvo.Admin.Components.Data;
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
            /* The Fields tab's reading too (FieldBadges), so a badge and a mask cannot disagree about one field. */
            var kind = PolicyOf(field.Value, key);
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

    /// <summary>
    /// How one field declares a <c>boolOrCel</c> policy: <see cref="JsonValueKind.True"/> for every caller,
    /// <see cref="JsonValueKind.String"/> for a CEL expression, or <see langword="null"/> when it declares none.
    /// </summary>
    /// <remarks>
    /// The one reading of <c>hidden</c>/<c>readOnly</c> the Fields tab and the Data screen share: <c>false</c> is
    /// no policy, and so is anything the schema would refuse, which the apply explains better than a badge.
    /// </remarks>
    /// <param name="field">The field's declaration, when there is one.</param>
    /// <param name="key"><c>hidden</c> or <c>readOnly</c>.</param>
    public static JsonValueKind? PolicyOf(JsonElement? field, string key)
        => field is { } declared && KindOf(declared, key) is var kind and (JsonValueKind.True or JsonValueKind.String)
            ? kind
            : null;

    /// <summary>Each field one entity declares, by name, as its declaration's JSON.</summary>
    /// <param name="descriptorJson">The descriptor, applied or working.</param>
    /// <param name="entity">The entity to read.</param>
    /// <returns>The declarations, cloned to outlive the parse; empty when the entity declares none.</returns>
    public static IReadOnlyDictionary<string, JsonElement> FieldDeclarations(string descriptorJson, string entity)
    {
        using var document = Parse(descriptorJson);
        if (document is null
            || !document.RootElement.TryGetProperty("entities", out var entities)
            || entities.ValueKind != JsonValueKind.Object
            || !entities.TryGetProperty(entity, out var declared)
            || declared.ValueKind != JsonValueKind.Object
            || !declared.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        return fields.EnumerateObject()
            .ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>The webhook endpoints a descriptor declares, by name, in its own order.</summary>
    /// <remarks>
    /// Read from the working copy by the pickers and Integrations (spec B5), so a declaration staged a minute ago is
    /// offered. A block of the wrong shape reads as none: the apply is the authority that refuses it.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor, applied or working.</param>
    /// <returns>Name to declaration, cloned to outlive the parse.</returns>
    public static IReadOnlyList<KeyValuePair<string, JsonElement>> Endpoints(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        return document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("webhooks", out var webhooks)
            && webhooks.ValueKind == JsonValueKind.Object
            && webhooks.TryGetProperty("endpoints", out var endpoints)
                ? Declarations(endpoints)
                : [];
    }

    /// <summary>The message templates a descriptor declares, by name, in its own order — a <c>bodyFile</c> one included.</summary>
    /// <param name="descriptorJson">The descriptor, applied or working.</param>
    /// <returns>Name to declaration, cloned to outlive the parse.</returns>
    public static IReadOnlyList<KeyValuePair<string, JsonElement>> Templates(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        return document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("templates", out var templates)
                ? Declarations(templates)
                : [];
    }

    /// <summary>One after-hook that posts to an endpoint or sends a template.</summary>
    /// <param name="Kind"><c>endpoint</c> or <c>template</c>.</param>
    /// <param name="Name">The declaration it names.</param>
    /// <param name="Entity">The entity the hook is on.</param>
    /// <param name="Point">The hook point.</param>
    /// <param name="Position">Its position within the point.</param>
    internal sealed record IntegrationUse(string Kind, string Name, string Entity, string Point, int Position);

    /// <summary>Every hook action that names an endpoint or a template, entity by entity.</summary>
    /// <remarks>
    /// Hooks only: an <c>automation</c> rule's action names an endpoint too, but no rule is evaluated in this build, so it
    /// delivers nothing and counting it would call an endpoint used that receives nothing.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor, applied or working.</param>
    /// <returns>The uses, in the descriptor's own order.</returns>
    public static IReadOnlyList<IntegrationUse> IntegrationUses(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document is null
            || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("entities", out var entities)
            || entities.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. entities.EnumerateObject().SelectMany(entity => UsesOf(entity.Name, entity.Value))];
    }

    /// <summary>Whether a template declaration reads its body from a file, which this build refuses to send.</summary>
    /// <param name="template">One template's declaration.</param>
    /// <returns><see langword="true"/> when it is an object with a <c>bodyFile</c> key, whatever its value.</returns>
    internal static bool HasBodyFile(JsonElement template)
        => template.ValueKind == JsonValueKind.Object && template.TryGetProperty("bodyFile", out _);

    /// <summary>One property's text, or nothing when the owner is not an object or the property is not a string.</summary>
    /// <param name="owner">The object to read.</param>
    /// <param name="name">The property.</param>
    /// <returns>The string value, or <see langword="null"/>.</returns>
    internal static string? TextOf(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object
           && owner.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<KeyValuePair<string, JsonElement>> Declarations(JsonElement block)
        => block.ValueKind == JsonValueKind.Object
            ? [.. block.EnumerateObject().Select(pair => new KeyValuePair<string, JsonElement>(pair.Name, pair.Value.Clone()))]
            : [];

    private static IEnumerable<IntegrationUse> UsesOf(string entity, JsonElement declared)
    {
        if (declared.ValueKind != JsonValueKind.Object
            || !declared.TryGetProperty("hooks", out var hooks)
            || hooks.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var point in hooks.EnumerateObject().Where(point => point.Value.ValueKind == JsonValueKind.Array))
        {
            var position = 0;
            foreach (var hook in point.Value.EnumerateArray())
            {
                if (UseOf(entity, point.Name, position++, hook) is { } use)
                {
                    yield return use;
                }
            }
        }
    }

    private static IntegrationUse? UseOf(string entity, string point, int position, JsonElement hook)
    {
        if (hook.ValueKind != JsonValueKind.Object || !hook.TryGetProperty("action", out var action))
        {
            return null;
        }

        if (TextOf(action, "endpoint") is { } endpoint)
        {
            return new IntegrationUse("endpoint", endpoint, entity, point, position);
        }

        return TextOf(action, "template") is { } template ? new IntegrationUse("template", template, entity, point, position) : null;
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
