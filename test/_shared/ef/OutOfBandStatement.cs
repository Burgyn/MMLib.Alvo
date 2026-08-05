using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// Runs one raw statement against a started database on a connection of its own, and hands back the engine's own
/// failure instead of throwing it.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>AlvoDataComputedRollupTests.ExecuteOutOfBandAsync</c> seam's whole implementation, and it is
/// shared between the two engine legs because nothing in it is engine-specific: the driver's own context factory
/// already knows how to reach the database the port is using, which is exactly what "out of band, same store"
/// needs. Each call gets a fresh context and therefore a fresh connection, so the statement is not riding
/// anything the port has open.
/// </para>
/// <para>
/// It exists because "the column is a stored generated column" is a claim about the <em>engine</em>. Asking the
/// port proves only what the port does, and reading the value back is satisfied by an ordinary column somebody
/// filled in correctly — so the only question that discriminates is one the engine itself has to refuse, sent on
/// a connection <c>IAlvoData</c> is not mediating.
/// </para>
/// </remarks>
internal static class OutOfBandStatement
{
    /// <summary>Executes <paramref name="sql"/> and returns the engine's failure, or <see langword="null"/>.</summary>
    /// <param name="contexts">The driver's own context factory, for the database under test.</param>
    /// <param name="sql">The statement. Identifiers are double-quoted, which both shipped engines accept.</param>
    /// <param name="cancellationToken">The ambient test cancellation token.</param>
    internal static async Task<Exception?> ExecuteAsync(
        AlvoDataContextFactory contexts, string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        using var context = contexts.Create();
        try
        {
            await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            return null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return failure;
        }
    }
}
