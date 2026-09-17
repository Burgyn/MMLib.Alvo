using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The outbox insert's own precondition, asserted without a database: the insert runs on the write
/// transaction's connection, so a broken caller must be refused before anything is composed onto it rather
/// than by a <see cref="NullReferenceException"/> raised mid-statement inside someone else's transaction.
/// </summary>
public class OutboxTableInsertTests
{
    /// <summary>There is no row to write without an envelope, and the refusal says which argument was missing.</summary>
    [Fact]
    public async Task An_insert_requires_the_envelope()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");

        var refused = await Should.ThrowAsync<ArgumentNullException>(
            async () => await OutboxTable.InsertAsync(
                connection, transaction: null, OutboxTable.NameFor("alvo"), null!, TestContext.Current.CancellationToken));

        refused.ParamName.ShouldBe("@event");
    }
}
