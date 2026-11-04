using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The relational <see cref="IHookRejectionLog"/> (HX-09 Ruling 8): outside a unit a rejection is inserted at once; inside one
/// it is handed to the unit, and the driver flushes it after the unit has released its write turn and left the flow. A unit
/// already released keeps nothing more, so a rejection that raced its release is inserted at once.
/// </summary>
/// <remarks>
/// <para>
/// Every insert is autocommit on a connection of its own, behind the database's write gate, as the command ledger writes. A
/// failed insert is logged and swallowed: the log is a record of answers already given, and a storage hiccup must not turn a
/// 422 into a 500. A crash between a unit's rollback and its flush loses the row.
/// </para>
/// <para>The table is created once per process by <see cref="OnceDdl"/>, inside the write gate.</para>
/// </remarks>
internal sealed partial class EfCoreHookRejectionLog : IHookRejectionLog
{
    private readonly RelationalConnectionFactory _connections;
    private readonly string _table;
    private readonly ILogger<EfCoreHookRejectionLog> _logger;
    private readonly OnceDdl _ddl;

    internal EfCoreHookRejectionLog(RelationalConnectionFactory connections, string schemaPrefix, ILogger<EfCoreHookRejectionLog> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPrefix);
        ArgumentNullException.ThrowIfNull(logger);
        _connections = connections;
        _table = HookRejectionTable.NameFor(schemaPrefix);
        _logger = logger;
        _ddl = new OnceDdl(EnsureTableAsync);
    }

    internal static EfCoreHookRejectionLog ForTests(Func<DbConnection> connect, string schemaPrefix, ILogger<EfCoreHookRejectionLog> logger) =>
        new(new RelationalConnectionFactory(connect), schemaPrefix, logger);

    /// <inheritdoc/>
    public Task RecordAsync(HookRejection rejection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return AlvoFlow.Unit is WriteUnit unit && unit.Remember(rejection) ? Task.CompletedTask : InsertAsync(rejection, cancellationToken);
    }

    /// <summary>Inserts every rejection a unit remembered, once the unit has ended and left the flow.</summary>
    /// <param name="rejections">The unit's <see cref="WriteUnit.Rejections"/>.</param>
    /// <returns>A task that completes when every row is recorded or its failure logged.</returns>
    internal async Task FlushAsync(IReadOnlyList<HookRejection> rejections)
    {
        foreach (var rejection in rejections)
        {
            await InsertAsync(rejection, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The log records answers already given; no storage failure may change the answer (Ruling 8).")]
    private async Task InsertAsync(HookRejection rejection, CancellationToken cancellationToken)
    {
        try
        {
            await _ddl.EnsureAsync(cancellationToken).ConfigureAwait(false);
            await InsertGatedAsync(rejection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            HookRejectionNotRecorded(_logger, rejection.Extension, failure.GetType().FullName ?? failure.GetType().Name);
        }
    }

    private async Task InsertGatedAsync(HookRejection rejection, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                await HookRejectionTable.InsertAsync(connection, _table, rejection, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                await HookRejectionTable.EnsureAsync(connection, _table, ct).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A rejection by write hook {Extension} could not be recorded ({ExceptionType}); the caller's answer is unchanged.")]
    private static partial void HookRejectionNotRecorded(ILogger logger, string extension, string exceptionType);
}
