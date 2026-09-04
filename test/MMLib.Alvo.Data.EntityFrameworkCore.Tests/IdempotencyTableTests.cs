using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What the idempotency record's row column holds. The column is <c>TEXT</c> and keeps its name, so
/// widening it to a list is a change to what the text means and to nothing else — which is the whole
/// reason it is safe against a database the framework created with <c>CREATE TABLE IF NOT EXISTS</c>.
/// </summary>
public sealed class IdempotencyTableTests
{
    /// <summary>One write's row list round-trips.</summary>
    [Fact]
    public void One_row_round_trips()
    {
        var id = Guid.NewGuid();

        IdempotencyTable.Decode(IdempotencyTable.Encode([id])).ShouldBe([id]);
    }

    /// <summary>A batch's does too, in the order the batch wrote them.</summary>
    /// <remarks>
    /// Order is asserted rather than membership, because a replay answers the rows in the order the first
    /// request wrote them and a caller correlates them with the rows they sent by position.
    /// </remarks>
    [Fact]
    public void Many_rows_round_trip_in_order()
    {
        IReadOnlyList<Guid> ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        IdempotencyTable.Decode(IdempotencyTable.Encode(ids)).ShouldBe(ids, ignoreOrder: false);
    }

    /// <summary>A record written before this widening holds a bare GUID, and it is still readable.</summary>
    /// <remarks>
    /// Nothing is released, so this is a courtesy to a developer's existing local database rather than a
    /// compatibility obligation — but without it the first replay against such a database throws inside the
    /// write transaction, which the contended-write retry then retries ten times before surfacing it as an
    /// unattributable 500.
    /// </remarks>
    [Fact]
    public void A_record_written_before_the_widening_is_still_one_row()
    {
        var id = Guid.NewGuid();

        IdempotencyTable.Decode(id.ToString()).ShouldBe([id]);
    }

    /// <summary>
    /// The encoded form is a JSON array, which is what makes the reader's one-character test between the
    /// two shapes sound.
    /// </summary>
    [Fact]
    public void The_encoded_form_is_a_json_array()
    {
        IdempotencyTable.Encode([Guid.Empty]).ShouldStartWith("[");
    }
}
