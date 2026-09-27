namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// What the Schema screens say about an entity declared <c>storage: dynamic</c>, which this build never creates
/// (docs/todo-admin.md §8d item 21).
/// </summary>
/// <remarks>
/// <b>Why not "not applied yet".</b> The mapper drops a dynamic entity on every apply, because the dynamic driver is
/// F7 (#41), so it is never in <c>GET schema</c> and the list drew it as waiting for an apply forever — an apply the
/// operator could run as often as they liked. The entity header adds the build's own sentence, served as
/// <c>entity.storage</c> in <c>capabilities.warned</c>; these two strings are the dashboard's labels around it.
/// </remarks>
internal static class DynamicStorageWords
{
    /// <summary>The badge a dynamic entity carries where a pending one says "not applied yet".</summary>
    public const string Badge = "dynamic — not honoured by this build (F7, #41)";

    /// <summary>The list row's line under the name.</summary>
    public const string ListNote =
        "Declared with storage: dynamic. This build has no dynamic driver, so no apply creates a table or an API for it.";
}
