namespace MMLib.Alvo.Admin.Components.Access;

/// <summary>
/// Where the people list is: a search, and the cursors that led to the page on screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cursors, not page numbers</b>, because the port pages by an opaque <c>After</c> (<c>AlvoUserQuery</c>) and says
/// only where the next page starts — keyset paging, which the identity store does over the ordered address
/// (<c>AlvoIdentityUserAdministration.ListAsync</c>), so a page does not skip a row under a concurrent create the way
/// an offset would. Going back is therefore the cursor that led here, kept on a stack; a new search starts from the
/// first page, because a cursor belongs to the query that produced it.
/// </para>
/// <para>
/// <b>It widens nothing.</b> The search and the cursor narrow the list the port already hands this caller, and the
/// port's guard decides who may list at all whatever the query says (<c>GuardedUserAdministration.ListAsync</c>).
/// </para>
/// </remarks>
internal sealed class PeoplePaging
{
    /// <summary>The port's own page size.</summary>
    private const int Limit = 50;

    private readonly Stack<string?> _earlier = new();
    private string? _after;

    /// <summary>The address fragment searched for, or empty.</summary>
    public string Search { get; private set; } = string.Empty;

    /// <summary>Whether there is a page before this one.</summary>
    public bool HasEarlier => _earlier.Count > 0;

    /// <summary>What to ask the port for.</summary>
    public AlvoUserQuery Query => new(Search.Length > 0 ? Search : null, Limit, _after);

    /// <summary>Searches from the first page.</summary>
    /// <param name="search">What was typed; surrounding space is not part of an address.</param>
    public void Find(string search)
    {
        Search = search.Trim();
        _after = null;
        _earlier.Clear();
    }

    /// <summary>Moves to the page that starts at <paramref name="cursor"/>.</summary>
    /// <param name="cursor">The page's <c>NextCursor</c>.</param>
    public void Next(string cursor)
    {
        _earlier.Push(_after);
        _after = cursor;
    }

    /// <summary>Moves back to the page before this one.</summary>
    public void Previous()
    {
        if (_earlier.Count > 0)
        {
            _after = _earlier.Pop();
        }
    }
}
