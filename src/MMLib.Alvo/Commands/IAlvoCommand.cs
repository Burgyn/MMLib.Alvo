namespace MMLib.Alvo.Commands;

/// <summary>
/// A command invoked in process — by an event handler, a timer or a job — through <see cref="IAlvoCommands"/>, under its
/// access rule, at most one transaction unit and its completion event, exactly as a command mapped over HTTP.
/// </summary>
/// <typeparam name="TInput">What the command is invoked with.</typeparam>
/// <remarks>Register it with <c>AddCommand&lt;TCommand&gt;(name, access)</c>; it is resolved from a fresh scope per invocation.</remarks>
public interface IAlvoCommand<in TInput>
{
    /// <summary>Runs the command.</summary>
    /// <param name="input">What it was invoked with.</param>
    /// <param name="context">Who invoked it, a session as them, and its one unit.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>A task that completes when the command has run.</returns>
    Task HandleAsync(TInput input, AlvoCommandContext context, CancellationToken cancellationToken);
}
