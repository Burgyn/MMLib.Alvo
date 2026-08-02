using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Diagnostics;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// Records every SQL statement EF executes against one database, so a test can assert on the
/// <em>statement</em> — that the policy predicate is in its <c>WHERE</c>, that a masked column is
/// NULL-projected, that a patch is one <c>UPDATE</c> — rather than only on the rows that came back.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to EF Core's own <see cref="DiagnosticListener"/> through
/// <see cref="DiagnosticListener.AllListeners"/> rather than registering a <c>DbCommandInterceptor</c>: an
/// interceptor has to be attached to the <c>DbContextOptions</c>, and the data path's options are built by
/// the production <c>UseSqlite</c> registration. Observing from outside means these tests watch the
/// <b>production</b> configuration instead of a fixture-built copy of it.
/// </para>
/// <para>
/// The listener is process-wide, so events are filtered to this fixture's own database — every
/// <c>StartAsync</c> creates a uniquely named one — and test classes stay safe to run in parallel.
/// </para>
/// <para>
/// Nothing here is SQLite-specific, which is why it is linked into both engine test projects rather than
/// living in one: "the policy predicate is in the <c>WHERE</c>, never an in-memory post-filter" is a
/// per-engine acceptance criterion, and a capture that only one engine owned left the other proving it by
/// outcomes — which a materialise-then-filter implementation satisfies exactly.
/// </para>
/// </remarks>
internal sealed class SqlCapture : IObserver<DiagnosticListener>, IDisposable
{
    private const string EfListenerName = "Microsoft.EntityFrameworkCore";
    private const string CommandExecutingEventName = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting";

    private readonly List<string> _statements = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gate = new();
    private readonly string _marker;
    private bool _disposed;

    /// <summary>Starts recording the statements of the one database <paramref name="marker"/> identifies.</summary>
    /// <param name="marker">
    /// A substring unique to that database's connection string — its file name on SQLite, its generated
    /// database name on PostgreSQL. Both fixtures already mint a unique one per <c>StartAsync</c>.
    /// </param>
    internal SqlCapture(string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        _marker = marker;
        _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }

    internal IReadOnlyList<string> Statements
    {
        get
        {
            lock (_gate)
            {
                return [.. _statements];
            }
        }
    }

    internal string LastStatement
    {
        get
        {
            var statements = Statements;
            return statements.Count == 0
                ? throw new InvalidOperationException("No statement was executed against this database.")
                : statements[^1];
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _statements.Clear();
        }
    }

    public void OnNext(DiagnosticListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (!string.Equals(listener.Name, EfListenerName, StringComparison.Ordinal))
        {
            return;
        }

        var subscription = listener.Subscribe(new EventSink(this));
        lock (_gate)
        {
            if (_disposed)
            {
                subscription.Dispose();
                return;
            }

            _subscriptions.Add(subscription);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    /// <summary>
    /// Unsubscribes, taking the list under the same lock every other member uses. The listener is
    /// process-wide, so <see cref="OnNext(DiagnosticListener)"/> can be adding a subscription on another
    /// test's thread while this runs — enumerating the live list instead would throw there.
    /// </summary>
    public void Dispose()
    {
        IReadOnlyList<IDisposable> subscriptions;
        lock (_gate)
        {
            _disposed = true;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// Records one statement, trimmed at the ends only: EF's SQL generator appends a trailing newline, which
    /// is not information, while every separator inside the text is.
    /// </summary>
    private void Record(CommandEventData command)
    {
        if (command.Command.Connection?.ConnectionString.Contains(_marker, StringComparison.Ordinal) != true)
        {
            return;
        }

        lock (_gate)
        {
            _statements.Add(command.Command.CommandText.Trim());
        }
    }

    private sealed class EventSink(SqlCapture owner) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (string.Equals(value.Key, CommandExecutingEventName, StringComparison.Ordinal)
                && value.Value is CommandEventData command)
            {
                owner.Record(command);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
