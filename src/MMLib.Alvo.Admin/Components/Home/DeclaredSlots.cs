using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Home;

/// <summary>
/// Which of the build's warned slots one descriptor really declares — a top-level block, or one of the three
/// qualified keys inside an honoured block (<c>auth.providers</c>, <c>entity.storage</c>, <c>entity.realtime</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A block declined by value is not a declaration</b>, the core's rule for its own warning
/// (<c>UnhonouredSubsystems</c>): <c>dynamicEntities</c> counts only with <c>enabled: true</c>, <c>webhooks</c>
/// only with an endpoint, and any other block only when it is not empty. Intersecting on the key alone listed
/// <c>dynamicEntities</c> for <c>enabled: false</c> (docs/todo-admin.md §5e 13, #271).
/// </para>
/// <para>
/// <b>Restated, not shared, and pinned rather than trusted.</b> The core's predicates are internal to
/// <c>MMLib.Alvo</c>, which this assembly does not reference. A top-level block this file does not know falls back
/// to "present and not empty", so a block the core warns about tomorrow still reaches the Overview; a qualified slot
/// has no such fallback. <c>MMLib.Alvo.Host.Tests.DeclaredSlotsAgreementTests</c> holds both halves to the real
/// build: <see cref="Qualified"/> against the report's slot names, and <see cref="Declares"/> against the core's own
/// predicate for every reported row, over every example descriptor and the cases declined by value.
/// </para>
/// </remarks>
internal static class DeclaredSlots
{
    /// <summary>A provider other than <c>local</c> is declared.</summary>
    public const string AuthProviders = "auth.providers";

    /// <summary>An entity declares <c>storage: dynamic</c>.</summary>
    public const string EntityStorage = "entity.storage";

    /// <summary>
    /// An entity declares <c>realtime: true</c>. The schema's default is also true, but that is a fact about the build,
    /// which Settings says once under "This build"; the Overview says what the project declared.
    /// </summary>
    public const string EntityRealtime = "entity.realtime";

    /// <summary>Every qualified slot this file can detect.</summary>
    public static IReadOnlyList<string> Qualified { get; } = [AuthProviders, EntityStorage, EntityRealtime];

    /// <summary>Whether <paramref name="descriptor"/> declares <paramref name="slot"/>, declined values excluded.</summary>
    /// <param name="descriptor">The descriptor's root object.</param>
    /// <param name="slot">A warned block's name or qualified slot, as the build reports it.</param>
    public static bool Declares(JsonElement descriptor, string slot) => slot switch
    {
        AuthProviders => Member(descriptor, "auth") is { } auth
            && Member(auth, "providers") is { ValueKind: JsonValueKind.Array } providers
            && providers.EnumerateArray().Any(provider => provider.ValueKind != JsonValueKind.String
                || provider.GetString() != "local"),
        EntityStorage => Entities(descriptor).Any(entity => Text(entity, "storage") == "dynamic"),
        EntityRealtime => Entities(descriptor).Any(entity =>
            Member(entity, "realtime") is { ValueKind: JsonValueKind.True }),
        "dynamicEntities" => Member(descriptor, slot) is { } block
            && Member(block, "enabled") is { ValueKind: JsonValueKind.True },
        "webhooks" => Member(descriptor, slot) is { } webhooks && NotEmpty(Member(webhooks, "endpoints")),
        _ => NotEmpty(Member(descriptor, slot)),
    };

    /// <summary>A property of an object, or <see langword="null"/> when the owner is not one or lacks it.</summary>
    internal static JsonElement? Member(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) ? value : null;

    /// <summary>A string property's value, or <see langword="null"/>.</summary>
    internal static string? Text(JsonElement owner, string name)
        => Member(owner, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Whether a value says something: a non-empty object, array or string, a number, or <c>true</c>.</summary>
    internal static bool NotEmpty(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Object } found => found.EnumerateObject().Any(),
        { ValueKind: JsonValueKind.Array } found => found.GetArrayLength() > 0,
        { ValueKind: JsonValueKind.String } found => found.GetString() is { Length: > 0 },
        { ValueKind: JsonValueKind.Number or JsonValueKind.True } => true,
        _ => false,
    };

    /// <summary>The declared entities that are objects.</summary>
    private static IEnumerable<JsonElement> Entities(JsonElement descriptor)
        => Member(descriptor, "entities") is { ValueKind: JsonValueKind.Object } entities
            ? entities.EnumerateObject().Select(entity => entity.Value).Where(entity => entity.ValueKind == JsonValueKind.Object)
            : [];
}
