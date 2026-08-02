using MMLib.Alvo.Data;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// An <see cref="IAlvoData"/> plus the SQL its engine actually executed — the seam
/// <see cref="AlvoDataStatementTests"/> needs, because "the policy predicate is in the <c>WHERE</c>, never an
/// in-memory post-filter" is a claim about the emitted statement and is invisible in the rows that come back.
/// </summary>
public interface IStatementProbe
{
    /// <summary>Gets the data port under test.</summary>
    IAlvoData Data { get; }

    /// <summary>Gets every statement this engine has executed since the last <see cref="ClearStatements"/>.</summary>
    IReadOnlyList<string> Statements { get; }

    /// <summary>Forgets every recorded statement, so a fact asserts on the ones its own act produced.</summary>
    void ClearStatements();
}
