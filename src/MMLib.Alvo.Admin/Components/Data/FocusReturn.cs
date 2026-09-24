namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// Whether focus goes back to the row that opened a record's sheet, now that the sheet has closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The WAI-ARIA dialog pattern</b>: a dialog that closes returns focus to what opened it. Without it focus
/// falls to the document, the selected row's highlight no longer says where the keyboard is, and the next
/// Enter opens a row nobody is looking at.
/// </para>
/// <para>
/// <b>Watched across renders, not assumed.</b> The grid learns the sheet is open from the screen that owns it,
/// one render after the row asked; so this waits to <em>see</em> the sheet open before a closed one counts,
/// and a sheet this grid did not open (a linked record, a new one) never sends focus to a row.
/// </para>
/// </remarks>
internal sealed class FocusReturn
{
    private bool _opened;
    private bool _seenOpen;

    /// <summary>A row of this grid opened the sheet.</summary>
    public void Opened()
    {
        _opened = true;
        _seenOpen = false;
    }

    /// <summary>Reads the sheet's state after a render; answers whether focus should go back now.</summary>
    /// <param name="sheetOpen">Whether a record's sheet is on the page.</param>
    public bool Observe(bool sheetOpen)
    {
        if (!_opened)
        {
            return false;
        }

        if (sheetOpen)
        {
            _seenOpen = true;
            return false;
        }

        if (!_seenOpen)
        {
            return false;
        }

        _opened = false;
        return true;
    }
}
