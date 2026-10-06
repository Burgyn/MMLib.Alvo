using MudBlazor;

namespace MMLib.Alvo.Admin.Components.Schema;

/* Focus stays on a select inside the sheet after its list closes (spec §3.2, §11) — one helper for every select here. */
public partial class HooksTab
{
    /// <summary>The sheet's selects by id — the mutate rows', the pickers' — for <see cref="RefocusSelectAsync"/>.</summary>
    private readonly Dictionary<string, MudSelect<string>> _selects = new(StringComparer.Ordinal);

    /// <summary>Gives focus back to a select once its choice is drawn (<see cref="SelectFocus"/>).</summary>
    /// <param name="id">The select's id in <see cref="_selects"/>.</param>
    private Task RefocusSelectAsync(string id) => SelectFocus.AfterChoice(_selects, id, Logger);
}
