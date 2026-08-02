namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class KeysetCursorTests
{
    [Fact]
    public void A_cursor_round_trips()
    {
        var id = Guid.NewGuid();

        KeysetCursor.TryDecode(KeysetCursor.Encode(id), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(id);
    }

    /// <summary>
    /// Not <c>ShouldNotContain("-")</c>: base64url's own alphabet contains <c>-</c>, so half of all row ids
    /// encode to a cursor holding one and that assertion would fail on a coin flip. What the cursor must not
    /// be is the row id in any readable form.
    /// </summary>
    [Fact]
    public void A_cursor_is_opaque_rather_than_the_bare_id()
    {
        var id = Guid.NewGuid();
        var cursor = KeysetCursor.Encode(id);

        cursor.ShouldNotBe(id.ToString());
        cursor.ShouldNotContain(id.ToString("N"), Case.Insensitive);
    }

    [Fact]
    public void Two_ids_encode_to_two_cursors()
        => KeysetCursor.Encode(Guid.NewGuid()).ShouldNotBe(KeysetCursor.Encode(Guid.NewGuid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void A_malformed_or_forged_cursor_is_rejected_rather_than_throwing(string? cursor)
        => KeysetCursor.TryDecode(cursor, out _).ShouldBeFalse();

    [Fact]
    public void A_rejected_cursor_yields_the_default_row_id_rather_than_a_stale_one()
    {
        KeysetCursor.TryDecode("YWJj", out var rowId).ShouldBeFalse();

        rowId.ShouldBe(Guid.Empty);
    }
}
