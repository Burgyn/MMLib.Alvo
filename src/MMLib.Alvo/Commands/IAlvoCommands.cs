namespace MMLib.Alvo.Commands;

/// <summary>Invokes the host's internal commands by name.</summary>
public interface IAlvoCommands
{
    /// <summary>
    /// Invokes the internal command <paramref name="name"/> as <paramref name="context"/>: its access rule is evaluated
    /// first, then it runs, and once it completes its <c>alvo.command.&lt;name&gt;.completed</c> event is recorded.
    /// </summary>
    /// <typeparam name="TInput">The input type the command takes, or one derived from it.</typeparam>
    /// <param name="name">The command's name.</param>
    /// <param name="input">Its input.</param>
    /// <param name="context">The caller it runs as — in an event handler, <c>AlvoEventContext.Caller</c>.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>A task that completes when the command has run and its completion is recorded.</returns>
    /// <exception cref="ArgumentException">No internal command has that name, or it takes another input type.</exception>
    /// <exception cref="MMLib.Alvo.Data.AlvoAuthorizationException">The command's access rule does not admit the caller.</exception>
    /// <exception cref="MMLib.Alvo.Data.AlvoUnitInProgressException">A transaction unit is open on this flow.</exception>
    /// <exception cref="MMLib.Alvo.Data.AlvoChainDepthExceededException">The caller's chain is already past the depth limit.</exception>
    Task InvokeAsync<TInput>(string name, TInput input, AlvoContext context, CancellationToken cancellationToken = default);
}
