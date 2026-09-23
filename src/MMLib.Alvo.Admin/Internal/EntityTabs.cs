using Microsoft.AspNetCore.WebUtilities;

namespace MMLib.Alvo.Admin.Internal;

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

    /// <summary>The tabs, in the order they are drawn, each with its slug.</summary>
    public static IReadOnlyList<EntityTab> All { get; } =
    [
        new("Fields", "fields"),
        new("Relationships", "relationships"),
        new("Rules", "rules"),
        new("On write", "on-write"),
        new("Indexes", "indexes"),
        new("API", "api"),
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

    /// <summary>
    /// The tab an arrow, Home or End key moves to from <paramref name="current"/>, or
    /// <see langword="null"/> for any other key.
    /// </summary>
    /// <remarks>
    /// The WAI-ARIA tabs pattern: the arrows wrap, because a strip of six has no reason to stop at
    /// either end, and Home and End reach the ends.
    /// </remarks>
    public static EntityTab? Move(EntityTab current, string key)
    {
        var at = IndexOf(current);
        return key switch
        {
            "ArrowRight" => All[(at + 1) % All.Count],
            "ArrowLeft" => All[(at - 1 + All.Count) % All.Count],
            "Home" => All[0],
            "End" => All[^1],
            _ => null,
        };
    }

    private static int IndexOf(EntityTab tab)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index] == tab)
            {
                return index;
            }
        }

        return 0;
    }
}

/// <summary>One of the entity screen's tabs.</summary>
/// <param name="Title">What the tab reads.</param>
/// <param name="Slug">What the address carries for it.</param>
internal sealed record EntityTab(string Title, string Slug);
