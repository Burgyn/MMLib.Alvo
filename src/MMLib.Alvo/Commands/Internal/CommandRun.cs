using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>What a command is invoked with: the command, the caller, the bound row and the key.</summary>
internal sealed record CommandInvocation(CommandRegistration Command, AlvoContext Caller, AlvoRecord? Record, CommandKey? Key)
{
    /// <summary>Gets the bound row's id, when there is a row.</summary>
    internal Guid? RowId => Record?[AlvoManagedColumns.Id] as Guid?;
}

/// <summary>
/// One run of a command: its at-most-one unit, and whether that unit committed. The handler is not wrapped in a transaction
/// (R-M7); the unit is the handler's to open.
/// </summary>
internal sealed class CommandRun(
    CommandInvocation invocation, IAlvoSession data, IAlvoTransactions? transactions, CommandCompletion completion)
{
    private const int Idle = 0;
    private const int Opened = 1;
    private const int Answered = 2;

    private int _state;
    private int _committed;

    internal CommandInvocation Invocation => invocation;

    internal string Name => invocation.Command.Id;

    internal AlvoContext Caller => invocation.Caller;

    internal AlvoRecord? Record => invocation.Record;

    internal IAlvoSession Data => data;

    internal CommandKey? Key => invocation.Key;

    /// <summary>Gets a value indicating whether the run's unit committed — its event and key mark with it.</summary>
    internal bool Committed => Volatile.Read(ref _committed) == 1;

    /// <summary>
    /// Gets a value indicating whether the run opened its unit and the unit did not commit — its writes rolled back, so
    /// whatever the handler answers afterwards, nothing completed (Ruling 43).
    /// </summary>
    internal bool OpenedWithoutCommit => Volatile.Read(ref _state) == Opened && !Committed;

    /// <summary>Opens the run's one unit, writing the completion and the key's commit mark last inside it.</summary>
    internal async Task<T> TransactAsync<T>(
        Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureFirstUnit();
        var units = transactions ?? throw new InvalidOperationException(
            "This host's data provider opens no transaction units (IAlvoTransactions), so a command cannot open one. Register UseSqlite(…) or UsePostgreSql(…).");

        var result = await units.TransactAsync(Caller, (unit, ct) => WorkedAsync(unit, work, ct), options, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _committed, 1);
        return result;
    }

    /// <summary>Ends the run: no unit may be opened after the handler answered.</summary>
    internal void Finish() => Interlocked.CompareExchange(ref _state, Answered, Idle);

    private async Task<T> WorkedAsync<T>(IAlvoTransaction unit, Func<IAlvoTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var result = await work(unit, ct).ConfigureAwait(false);
        await completion.WriteInUnitAsync(invocation, unit, ct).ConfigureAwait(false);
        return result;
    }

    private void EnsureFirstUnit()
    {
        var prior = Interlocked.CompareExchange(ref _state, Opened, Idle);
        if (prior == Answered)
        {
            throw new InvalidOperationException($"The command '{Name}' has already answered; its context can no longer open a transaction unit.");
        }

        if (prior == Opened)
        {
            throw new InvalidOperationException(
                $"The command '{Name}' already opened its transaction unit. A command opens at most one: put every write that must commit together into that one delegate, and make an external call before it.");
        }
    }
}
