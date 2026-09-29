using Microsoft.AspNetCore.WebUtilities;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// The entity screen's tabs, and the slug each one is addressed by in <c>?tab=</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tab is in the address because an operator sends it.</b> "Look at the rules on work_orders"
/// is a link somebody pastes into a chat, and a link that opens on Fields makes the reader hunt for
/// what they were sent to see — the same for a reload, and for Back after following a relationship.
/// </para>
/// <para>
/// <b>An unknown slug opens Fields rather than refusing.</b> A link written before a tab was renamed
/// still opens the entity it names, which is the half of it that is certainly still right.
/// </para>
/// </remarks>
internal static class EntityTabs
{
    /// <summary>The name of the query parameter the tab is carried in.</summary>
    public const string Parameter = "tab";

    /// <summary>The Fields tab's slug.</summary>
    /// <remarks>
    /// The slugs are constants so the screen can dispatch on them: a slug is the tab's address, kept
    /// stable for the links already sent, while a title is copy and free to change — which is why a
    /// switch over the titles once sent a renamed tab silently to its <c>default:</c> (F-14).
    /// </remarks>
    public const string Fields = "fields";

    /// <summary>The Relationships tab's slug.</summary>
    public const string Relationships = "relationships";

    /// <summary>The Rules tab's slug.</summary>
    public const string Rules = "rules";

    /// <summary>The On write tab's slug.</summary>
    public const string OnWrite = "on-write";

    /// <summary>The Indexes tab's slug.</summary>
    public const string Indexes = "indexes";

    /// <summary>The API tab's slug.</summary>
    public const string Api = "api";

    /// <summary>The tabs, in the order they are drawn, each with its slug.</summary>
    public static IReadOnlyList<EntityTab> All { get; } =
    [
        new("Fields", Fields),
        new("Relationships", Relationships),
        new("Rules", Rules),
        new("On write", OnWrite),
        new("Indexes", Indexes),
        new("API", Api),
    ];

    /// <summary>The tab a screen opens on when the address names none.</summary>
    public static EntityTab First => All[0];

    /// <summary>The tab an absolute address names, or the first one.</summary>
    public static EntityTab FromUri(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        return query.TryGetValue(Parameter, out var slug) ? FromSlug(slug.ToString()) : First;
    }

    /// <summary>The tab a slug names, case-insensitively, or the first one.</summary>
    public static EntityTab FromSlug(string? slug) =>
        All.FirstOrDefault(tab => string.Equals(tab.Slug, slug, StringComparison.OrdinalIgnoreCase)) ?? First;
}

/// <summary>One of the entity screen's tabs.</summary>
/// <param name="Title">What the tab reads.</param>
/// <param name="Slug">What the address carries for it.</param>
internal sealed record EntityTab(string Title, string Slug);
