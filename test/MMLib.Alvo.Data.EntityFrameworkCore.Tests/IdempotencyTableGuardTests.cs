using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What the row-column codec refuses. Both guards run on the write path inside the caller's transaction, where
/// the contended-write retry re-attempts anything that throws — so an argument fault that arrives as a
/// <see cref="NullReferenceException"/> or a <see cref="FormatException"/> is retried ten times and then
/// surfaced as an unattributable 500 instead of failing once and naming what was wrong.
/// </summary>
public sealed class IdempotencyTableGuardTests
{
    /// <summary>
    /// The refusal names the codec's own parameter, so a reader is pointed at the caller that passed nothing
    /// rather than at an interior sequence operator.
    /// </summary>
    [Fact]
    public void Encode_refuses_a_missing_row_list_naming_its_own_parameter()
    {
        var refusal = Should.Throw<ArgumentNullException>(() => IdempotencyTable.Encode(null!));

        refusal.ParamName.ShouldBe("rowIds");
    }

    /// <summary>A stored value that names no row is an argument fault, not a parse failure.</summary>
    /// <param name="stored">A column value carrying no row id.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Decode_refuses_stored_text_that_names_no_row(string stored)
        => Should.Throw<ArgumentException>(() => IdempotencyTable.Decode(stored));

    /// <summary>And a missing one is an argument fault too, rather than a null dereference.</summary>
    [Fact]
    public void Decode_refuses_missing_stored_text_naming_its_own_parameter()
    {
        var refusal = Should.Throw<ArgumentNullException>(() => IdempotencyTable.Decode(null!));

        refusal.ParamName.ShouldBe("stored");
    }
}
