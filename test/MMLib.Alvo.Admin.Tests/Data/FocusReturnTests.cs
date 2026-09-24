using MMLib.Alvo.Admin.Components.Data;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>
/// Focus goes back to the row once the sheet it opened has come and gone — and only then.
/// </summary>
public class FocusReturnTests
{
    [Fact]
    public void Focus_returns_once_the_sheet_the_row_opened_has_closed()
    {
        var focus = new FocusReturn();

        focus.Opened();

        focus.Observe(sheetOpen: false).ShouldBeFalse("the screen has not drawn the sheet yet");
        focus.Observe(sheetOpen: true).ShouldBeFalse();
        focus.Observe(sheetOpen: false).ShouldBeTrue();
        focus.Observe(sheetOpen: false).ShouldBeFalse("once per sheet");
    }

    [Fact]
    public void A_sheet_the_grid_did_not_open_sends_focus_nowhere()
    {
        var focus = new FocusReturn();

        focus.Observe(sheetOpen: true).ShouldBeFalse();
        focus.Observe(sheetOpen: false).ShouldBeFalse();
    }
}
