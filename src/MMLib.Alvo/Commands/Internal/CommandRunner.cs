using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>What a claim decided: the answer to give instead of running, or the invocation — with its fencing token — to run.</summary>
internal sealed record CommandClaimed(IResult? Early, CommandInvocation Invocation);

/// <summary>
/// Claims a command's key, starts its run, and finishes it: the bookkeeping after the handler is not cancellable — the
/// handler's effects already happened, so their record must not be lost to a client that hung up.
/// </summary>
internal sealed partial class CommandRunner(
    ICommandLedger? ledger,
    IAlvoTransactions? transactions,
    IAlvoSessions sessions,
    CommandCompletion completion,
    TimeProvider time,
    JsonSerializerOptions json,
    ILogger<CommandRunner> logger)
{
    private ICommandLedger Ledger => ledger ?? throw new InvalidOperationException(
        "This composition keeps no command ledger, so commands cannot run; the boot refuses this composition.");

    /// <summary>Starts a run as the invocation's caller.</summary>
    /// <exception cref="InvalidOperationException">The invocation carries a key its claim did not grant a token to.</exception>
    internal CommandRun Start(CommandInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Key is { Token: null })
        {
            throw new InvalidOperationException(
                $"The command '{invocation.Command.Id}' was started with an unclaimed Idempotency-Key: start the invocation ClaimAsync answered, which carries its fencing token.");
        }

        return new(invocation, sessions.For(invocation.Caller), transactions, completion);
    }

    /// <summary>
    /// Claims the invocation's key — not cancellable: a client that hung up mid-claim must not strand the key (Ruling 49) —
    /// and answers the response to give instead of running, or the invocation carrying its fencing token.
    /// </summary>
    /// <exception cref="AlvoIdempotencyConflictException">The key was spent on a different request.</exception>
    internal async Task<CommandClaimed> ClaimAsync(CommandInvocation invocation)
    {
        if (invocation.Key is not { } key)
        {
            return new CommandClaimed(null, invocation);
        }

        var claim = await Ledger.ClaimAsync(key, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        return claim.State switch
        {
            CommandClaimState.Claimed => new CommandClaimed(null, invocation with { Key = key with { Token = claim.Token } }),
            CommandClaimState.Answered => new CommandClaimed(new ReplayedAnswer(claim.Answer!), invocation),
            CommandClaimState.InFlight => new CommandClaimed(ProblemResultFactory.IdempotencyInFlight(), invocation),
            CommandClaimState.Unanswered => new CommandClaimed(ProblemResultFactory.IdempotencyUnanswered(), invocation),
            _ => throw new AlvoIdempotencyConflictException(),
        };
    }

    /// <summary>
    /// Gives up the key claimed for a request that was refused before its handler ran. A failure to release is logged, never
    /// thrown: the refusal is the answer, and the key frees itself when its lease runs out.
    /// </summary>
    internal async Task ReleaseAsync(CommandInvocation refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        if (refused.Key is not { } key)
        {
            return;
        }

        try
        {
            await Ledger.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception bookkeeping)
        {
            RefusedKeyNotReleased(logger, refused.Command.Id, bookkeeping);
        }
    }

    /// <summary>Runs the handler and records what followed from it.</summary>
    internal async Task<object?> FinishAsync(CommandRun run, Func<Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(handler);
        object? result;
        try
        {
            result = await handler().ConfigureAwait(false);
        }
        catch
        {
            await FailedAsync(run).ConfigureAwait(false);
            throw;
        }

        await AnsweredAsync(run, result).ConfigureAwait(false);
        return result;
    }

    private async Task FailedAsync(CommandRun run)
    {
        run.Finish();
        if (run.Key is not { } key)
        {
            return;
        }

        try
        {
            await (run.Committed
                ? Ledger.AnswerAsync(key, CommandAnswers.Failed, CancellationToken.None)
                : Ledger.ReleaseAsync(key, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception bookkeeping)
        {
            FailureNotRecorded(logger, run.Name, bookkeeping);
        }
    }

    private async Task AnsweredAsync(CommandRun run, object? result)
    {
        run.Finish();
        var answer = AnswerOf(run, result);

        if (run.Committed)
        {
            await AnswerCommittedAsync(run, answer).ConfigureAwait(false);
            return;
        }

        if (run.OpenedWithoutCommit)
        {
            UnitRolledBack(logger, run.Name);
            await ReleaseAsync(run).ConfigureAwait(false);
            return;
        }

        await (CommandAnswers.IsSuccess(result)
            ? Ledger.CompleteAsync(completion.EventOf(run.Invocation), run.Key, answer, time.GetUtcNow(), CancellationToken.None)
            : ReleaseAsync(run)).ConfigureAwait(false);
    }

    private CommandAnswer? AnswerOf(CommandRun run, object? result)
    {
        if (run.Key is null)
        {
            return null;
        }

        var answer = CommandAnswers.Of(result, json);
        if (answer is null)
        {
            ResultNotReplayable(logger, run.Name);
        }

        return answer;
    }

    private Task AnswerCommittedAsync(CommandRun run, CommandAnswer? answer) =>
        run.Key is { } key && answer is not null ? Ledger.AnswerAsync(key, answer, CancellationToken.None) : Task.CompletedTask;

    private Task ReleaseAsync(CommandRun run) =>
        run.Key is { } key ? Ledger.ReleaseAsync(key, CancellationToken.None) : Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command '{Command}' answered without its transaction unit reporting a commit; no completion is recorded and its Idempotency-Key is given back.")]
    private static partial void UnitRolledBack(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command '{Command}' answered with a result that cannot be replayed (no status, a value that cannot be serialised, or a body over 64 KiB); a retry with its Idempotency-Key will be refused, not answered.")]
    private static partial void ResultNotReplayable(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command '{Command}' refused a request before its handler ran, and giving its Idempotency-Key back failed; the key is held until its lease runs out, and the refusal is still the answer.")]
    private static partial void RefusedKeyNotReleased(ILogger logger, string command, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command '{Command}' threw, and recording that on its Idempotency-Key failed too; the handler's own exception is rethrown.")]
    private static partial void FailureNotRecorded(ILogger logger, string command, Exception exception);
}
