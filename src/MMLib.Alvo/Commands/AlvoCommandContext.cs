using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using System.Reflection;

namespace MMLib.Alvo.Commands;

/// <summary>
/// What a command's handler is given: who invoked it, the row it is bound to, a data session as that caller, and the one
/// transaction unit it may open.
/// </summary>
/// <remarks>
/// <para>
/// <b>The handler is not in a transaction.</b> Make an external call — a payment provider, a VAT registry — first, through
/// your own services, then write everything that must commit together in one <see cref="TransactAsync{T}"/>. Inside it, use
/// the unit it hands you: <c>IAlvoData</c> and <see cref="Data"/> are refused while it is open.
/// </para>
/// <para>
/// <b>At most one unit per run.</b> The command's completion event (<c>alvo.command.&lt;name&gt;.completed</c>) and its
/// <c>Idempotency-Key</c> record are written inside that unit as it commits, so they exist exactly when its writes do. A run
/// that opens no unit gets both written after it answers 2xx.
/// </para>
/// <para>
/// Take it as a parameter of a handler mapped with <c>MapAlvoCommand</c>; an internal command receives it in
/// <c>IAlvoCommand&lt;TInput&gt;.HandleAsync</c>. On any other endpoint its members throw.
/// </para>
/// </remarks>
public sealed class AlvoCommandContext : IBindableFromHttpContext<AlvoCommandContext>
{
    private static readonly object _slot = new();
    private CommandRun? _run;

    private AlvoCommandContext()
    {
    }

    internal AlvoCommandContext(CommandRun run) => _run = run;

    /// <summary>Gets the command's name.</summary>
    public string Name => Run.Name;

    /// <summary>Gets the caller the command runs as — its access rule admitted them.</summary>
    public AlvoContext Caller => Run.Caller;

    /// <summary>Gets a data session as <see cref="Caller"/>; each call commits on its own, and it is refused inside the unit.</summary>
    public IAlvoSession Data => Run.Data;

    /// <summary>
    /// Gets the row a command bound to an entity was invoked on, as the caller may read it; null for a command bound to none.
    /// </summary>
    public AlvoRecord? Record => Run.Record;

    /// <summary>Runs <paramref name="work"/> in the command's one transaction unit, as <see cref="Caller"/>.</summary>
    /// <typeparam name="T">What the work answers.</typeparam>
    /// <param name="work">The writes that commit together; it receives the unit and a token.</param>
    /// <param name="options">The unit's options; its time limit is 30 seconds unless set.</param>
    /// <param name="cancellationToken">A token to cancel the unit.</param>
    /// <returns>What <paramref name="work"/> answered, once the unit has committed.</returns>
    /// <exception cref="InvalidOperationException">This run already opened its unit, or has already answered.</exception>
    public Task<T> TransactAsync<T>(
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run.TransactAsync(work, options, cancellationToken);

    /// <summary>Runs <paramref name="work"/> in the command's one transaction unit, as <see cref="Caller"/>.</summary>
    /// <param name="work">The writes that commit together.</param>
    /// <param name="options">The unit's options.</param>
    /// <param name="cancellationToken">A token to cancel the unit.</param>
    /// <returns>A task that completes once the unit has committed.</returns>
    /// <exception cref="InvalidOperationException">This run already opened its unit, or has already answered.</exception>
    public Task TransactAsync(
        Func<IAlvoTransaction, CancellationToken, Task> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return Run.TransactAsync(
            async (unit, ct) =>
            {
                await work(unit, ct).ConfigureAwait(false);
                return true;
            },
            options,
            cancellationToken);
    }

    /// <summary>Binds the request's command context; <c>MapAlvoCommand</c>'s filter starts it before the handler runs.</summary>
    /// <param name="context">The request.</param>
    /// <param name="parameter">The handler parameter being bound.</param>
    /// <returns>The request's command context.</returns>
    public static ValueTask<AlvoCommandContext?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult<AlvoCommandContext?>(Pending(context));
    }

    /// <summary>The request's one command context, created unstarted on first use.</summary>
    internal static AlvoCommandContext Pending(HttpContext http)
    {
        if (http.Items.TryGetValue(_slot, out var existing) && existing is AlvoCommandContext context)
        {
            return context;
        }

        var created = new AlvoCommandContext();
        http.Items[_slot] = created;
        return created;
    }

    /// <summary>Starts the context with its run; a context starts once.</summary>
    internal void Start(CommandRun run) =>
        _run = _run is null ? run : throw new InvalidOperationException("This command context has already started.");

    private CommandRun Run => _run ?? throw new InvalidOperationException(
        "This AlvoCommandContext was bound on an endpoint MapAlvoCommand did not map, so no caller was resolved and no access "
        + "rule ran. Map the endpoint with MapAlvoCommand, or take an AlvoCaller on a plain endpoint with .WithAlvoCaller().");
}
