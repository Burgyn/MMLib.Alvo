using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// <see cref="IAlvoCommands"/>: refused inside an open unit (it would run beside it), depth-limited, access-ruled; the
/// command is resolved from a fresh scope and run like a mapped one, without an idempotency key (spec 2026-10-14 §4.9).
/// </summary>
internal sealed class AlvoCommands(
    ExtensionRegistry registry,
    CommandAccessJudge judge,
    AlvoChainDepthLimit depth,
    CommandRunner runner,
    IServiceScopeFactory scopes) : IAlvoCommands
{
    /// <inheritdoc/>
    public async Task InvokeAsync<TInput>(string name, TInput input, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(context);
        AlvoFlow.RefuseInsideUnit("IAlvoCommands.InvokeAsync");
        var command = Internal(name, typeof(TInput));
        depth.EnsureWithin(context);
        judge.EnsureAdmitted(command.Id, null, context);

        await RunAsync(command, input, context, cancellationToken).ConfigureAwait(false);
    }

    private CommandRegistration Internal(string name, Type input)
    {
        var command = registry.Commands.FirstOrDefault(candidate => !candidate.IsMapped && string.Equals(candidate.Id, name, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"No internal command '{name}' is registered. AddCommand<TCommand>(\"{name}\", …) registers one; a command mapped over HTTP is invoked over HTTP.",
                nameof(name));

        return typeof(IAlvoCommand<>).MakeGenericType(input).IsAssignableFrom(command.CommandType)
            ? command
            : throw new ArgumentException($"Command '{name}' takes {command.InputType!.Name}, not {input.Name}.", nameof(input));
    }

    private async Task RunAsync<TInput>(CommandRegistration command, TInput input, AlvoContext context, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = (IAlvoCommand<TInput>)scope.ServiceProvider.GetRequiredService(command.CommandType!);
        var run = runner.Start(new CommandInvocation(command, context, null, null));

        await runner.FinishAsync(run, async () =>
        {
            await handler.HandleAsync(input, new AlvoCommandContext(run), cancellationToken).ConfigureAwait(false);
            return null;
        }).ConfigureAwait(false);
    }
}
