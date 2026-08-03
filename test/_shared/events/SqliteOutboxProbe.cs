using Microsoft.Data.Sqlite;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Reads the outbox table straight out of a SQLite database file, without asking the host that wrote it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every crash fact needs a witness the dying process cannot supply.</b> "The event was committed and had not
/// been delivered at the moment of the kill" is a claim about rows on disk, and asking the host under test to
/// report its own queue state would make the claim depend on the very process the fact is about killing. This
/// opens the file directly, so it answers while the host is alive, while it is dead, and after it has been
/// replaced by a restart.
/// </para>
/// <para>
/// Unpooled on purpose, for the reason <c>AlvoHostWorld.TableNamesIn</c> records: a pooled probe connection
/// returns its SQLite handle to the process-wide pool rather than closing it, leaving the file open for a caller
/// that deletes it next.
/// </para>
/// <para>
/// A missing table is deliberately <em>not</em> absorbed into an empty result. A file with no
/// <c>alvo_outbox</c> in it means the host never booted far enough to create one, and that has to read as the
/// engine's own error rather than as "the queue is empty" — which is the shape every assertion here would then
/// pass on.
/// </para>
/// </remarks>
internal static class SqliteOutboxProbe
{
    /// <summary>Every outbox row in <paramref name="databasePath"/>, ascending by id.</summary>
    /// <param name="databasePath">The SQLite file the host under test was configured with.</param>
    /// <returns>The rows; empty when the file does not exist, because nothing ever opened it.</returns>
    internal static IReadOnlyList<OutboxRowState> Rows(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder($"Data Source={databasePath}") { Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = RowsSql;

        return Read(command);
    }

    private static List<OutboxRowState> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();

        var rows = new List<OutboxRowState>();
        while (reader.Read())
        {
            rows.Add(new OutboxRowState(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Claimed: !reader.IsDBNull(2),
                ClaimedBy: reader.IsDBNull(3) ? null : reader.GetString(3),
                Dispatched: !reader.IsDBNull(4),
                reader.GetInt32(5)));
        }

        return rows;
    }

    /// <summary>
    /// The framework outbox table's name under the default schema prefix.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than read from <c>OutboxTable.NameFor</c>, which is <see langword="internal"/> to the
    /// EF driver and unreachable from every project this file is linked into. <c>OutboxTableTests</c> is what
    /// holds the driver to the same spelling.
    /// </remarks>
    private const string OutboxTableName = "alvo_outbox";

    private const string RowsSql =
        $"SELECT id, event_type, claimed_at, claimed_by, dispatched_at, attempts FROM {OutboxTableName} ORDER BY id";
}

/// <summary>One outbox row, as much of its state machine as a crash fact reads.</summary>
/// <param name="Id">The event id, which is also the queue order and what a redelivery is identified by.</param>
/// <param name="EventType">The event type the row carries.</param>
/// <param name="Claimed">Whether <c>claimed_at</c> is set: some claimant holds this entry.</param>
/// <param name="ClaimedBy">Who holds it, so an abandoned claim is attributable to a process rather than to "something".</param>
/// <param name="Dispatched">Whether <c>dispatched_at</c> is set: the entry is retired and will never be claimed again.</param>
/// <param name="Attempts">How many times the entry has been claimed.</param>
internal sealed record OutboxRowState(
    Guid Id, string EventType, bool Claimed, string? ClaimedBy, bool Dispatched, int Attempts);
