using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Every address the dashboard links to, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>One table rather than an interpolation at every link.</b> Spelled by hand, an address is a
/// string that compiles either way and fails only in the browser — and the Preview and Transfer screens
/// once sat at <c>/admin/schema/preview</c> and <c>/admin/schema/transfer</c>, where the literal segment
/// beat <c>/admin/schema/{EntityName}</c> and made an entity legally named <c>preview</c> or
/// <c>transfer</c> unreachable (docs/architecture/admin-dashboard-review.md, F-12). Nothing under
/// <c>/admin/schema/</c> but an entity now, and <c>AdminPathsTests</c> pins every page's <c>@page</c>
/// template against this table, so the next such collision fails a unit test instead of a screen.
/// </para>
/// <para>
/// <b>Internal, and built on <see cref="AlvoAdmin.BasePath"/>.</b> The base path and the sign-in
/// addresses are the public contract a host spells; the screens' own addresses are implementation and
/// may move, which is exactly what this type is for.
/// </para>
/// </remarks>
internal static class AdminPaths
{
    /// <summary>Overview, the dashboard's root.</summary>
    public const string Overview = AlvoAdmin.BasePath;

    /// <summary>The first-run guide.</summary>
    public const string Welcome = $"{AlvoAdmin.BasePath}/welcome";

    /// <summary>The list of entities.</summary>
    public const string Schema = $"{AlvoAdmin.BasePath}/schema";

    /// <summary>Preview: the diff, the plan and the apply of what the working copy holds.</summary>
    public const string Changes = $"{AlvoAdmin.BasePath}/changes";

    /// <summary>Import and export of the whole descriptor.</summary>
    public const string Transfer = $"{AlvoAdmin.BasePath}/transfer";

    /// <summary>The list of entities to browse records of.</summary>
    public const string Data = $"{AlvoAdmin.BasePath}/data";

    /// <summary>Access: people and their roles.</summary>
    public const string Access = $"{AlvoAdmin.BasePath}/access";

    /// <summary>Configuration history: the applied revisions.</summary>
    public const string History = $"{AlvoAdmin.BasePath}/history";

    /// <summary>Integrations.</summary>
    public const string Integrations = $"{AlvoAdmin.BasePath}/integrations";

    /// <summary>Automations, declared and not yet built.</summary>
    public const string Automations = $"{AlvoAdmin.BasePath}/automations";

    /// <summary>Functions, declared and not yet built.</summary>
    public const string Functions = $"{AlvoAdmin.BasePath}/functions";

    /// <summary>Settings.</summary>
    public const string Settings = $"{AlvoAdmin.BasePath}/settings";

    /// <summary>The sign-in screen — the public constant, read rather than repeated.</summary>
    public const string SignIn = AlvoAdmin.SignInPath;

    /// <summary>An entity's schema screen, open on <paramref name="tab"/> when one is named.</summary>
    public static string Entity(string name, EntityTab? tab = null) => tab is null
        ? $"{Schema}/{Segment(name)}"
        : $"{Schema}/{Segment(name)}?{EntityTabs.Parameter}={tab.Slug}";

    /// <summary>An entity's records, with <paramref name="record"/>'s form open over them when one is named.</summary>
    public static string Records(string entity, Guid? record = null) => record is { } id
        ? $"{Data}/{Segment(entity)}?record={id}"
        : $"{Data}/{Segment(entity)}";

    /// <summary>Rules, on <paramref name="entity"/> when one is named.</summary>
    public static string Rules(string? entity = null) => entity is null
        ? $"{AlvoAdmin.BasePath}/rules"
        : $"{AlvoAdmin.BasePath}/rules/{Segment(entity)}";

    /// <summary>Whether an absolute address is <paramref name="path"/>, whatever its query.</summary>
    /// <remarks>
    /// A suffix of the path rather than the whole of it, so a host that mounts the application under a
    /// path base still matches; case-insensitive, because the router is.
    /// </remarks>
    public static bool IsAt(string uri, string path) => new Uri(uri).AbsolutePath.TrimEnd('/')
        .EndsWith(path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an absolute address is <paramref name="path"/> or a screen beneath it, whatever its query.</summary>
    /// <remarks>
    /// The navigation's prefix match, for <see cref="IsAt"/>'s reasons: a path base still matches, and so does
    /// any case. A whole segment, so <c>/admin/changes</c> does not claim <c>/admin/changeset</c>.
    /// </remarks>
    public static bool IsUnder(string uri, string path)
    {
        var absolute = new Uri(uri).AbsolutePath.TrimEnd('/');

        return absolute.EndsWith(path, StringComparison.OrdinalIgnoreCase)
            || absolute.Contains($"{path}/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A name as one path segment. The schema's name pattern makes this the identity for every name it
    /// admits; the escape is for the one it does not, so a bad name breaks its own link and nothing else.
    /// </summary>
    private static string Segment(string name) => Uri.EscapeDataString(name);
}
