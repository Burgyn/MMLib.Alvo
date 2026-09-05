namespace MMLib.Alvo.Data;

/// <summary>What one create-or-replace produced: the row, and which branch produced it.</summary>
/// <remarks>
/// <para>
/// <b><see cref="Created"/> exists because the caller has to answer <c>201</c> or <c>200</c>, and that
/// answer is not derivable from the row.</b> A created row and a replaced one are the same shape — same
/// fields, same id, and on a replacement even the same <c>created_at</c> — so a caller inspecting the
/// record cannot tell which happened. Only the port knows, so only the port can say.
/// </para>
/// <para>
/// <b>A replay answers <see cref="Created"/> <see langword="false"/>, always.</b> <c>201</c> reports an act
/// of creation, and a replayed request performs none: it reports the state a previous request left. The
/// idempotency record carries row ids rather than the branch that wrote them, and it deliberately gains
/// nothing to carry it: storing the branch grows the record for one header, and inferring it from
/// <c>created_at == updated_at</c> is a guess that a row replaced in the instant it was created defeats.
/// </para>
/// </remarks>
public sealed record AlvoReplaceResult
{
    /// <summary>Initializes a new instance of the <see cref="AlvoReplaceResult"/> class.</summary>
    /// <param name="Row">The row as it now stands.</param>
    /// <param name="Created">Whether the row did not exist and this write created it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="Row"/> is <see langword="null"/>.</exception>
    public AlvoReplaceResult(AlvoRecord Row, bool Created)
    {
        ArgumentNullException.ThrowIfNull(Row);

        this.Row = Row;
        this.Created = Created;
    }

    /// <summary>The row as it now stands.</summary>
    /// <remarks>
    /// <b>Get-only, not <c>init</c>, and that is what keeps the branch honest.</b> A <c>with</c> expression
    /// does not run the constructor, so an <c>init</c> setter would let
    /// <c>ReplacedRow(row) with { Created = true }</c> report a creation that never happened — and the
    /// endpoint would answer <c>201</c> with a <c>Location</c> for a row it did not create.
    /// </remarks>
    public AlvoRecord Row { get; }

    /// <inheritdoc cref="Row"/>
    /// <summary>Whether the row did not exist and this write created it.</summary>
    public bool Created { get; }

    /// <summary>The result of a write that created the row.</summary>
    /// <param name="row">The row it created.</param>
    public static AlvoReplaceResult CreatedRow(AlvoRecord row) => new(row, Created: true);

    /// <summary>The result of a write that replaced an existing row, and of every replay.</summary>
    /// <param name="row">The row as it now stands.</param>
    public static AlvoReplaceResult ReplacedRow(AlvoRecord row) => new(row, Created: false);
}
