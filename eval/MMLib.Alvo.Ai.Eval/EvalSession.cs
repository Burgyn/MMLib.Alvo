namespace MMLib.Alvo.Ai.Eval;

/// <summary>One eval run end to end: boot the world, run the suite, print the table, answer the exit status.</summary>
/// <remarks>
/// Exit status: <c>0</c> the suite passed, <c>1</c> it did not — including a host that failed to boot and a run
/// cancelled with Ctrl-C, which prints the partial table rather than losing it. Usage errors (<c>2</c>) are the
/// caller's, answered before anything boots.
/// </remarks>
internal static class EvalSession
{
    private const int Passed = 0;
    private const int Failed = 1;

    internal static async Task<int> RunAsync(EvalOptions options, CancellationToken ct)
    {
        if (await BootAsync(options, ct).ConfigureAwait(false) is not { } world)
        {
            return Failed;
        }

        await using (world.ConfigureAwait(false))
        {
            using var trace = options.TraceDirectory is { } directory ? EvalTrace.Open(directory, options) : null;
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, world.Stopping);
            var runs = new List<CaseRun>();
            var cancelled = await RunSuiteAsync(new EvalRunner(world, options, trace), runs, stopping.Token).ConfigureAwait(false);

            EvalReport.Print(options, runs, Console.Out, cancelled);
            if (trace is not null)
            {
                await Console.Error.WriteLineAsync($"[eval-assistant] trace: {trace.Path}").ConfigureAwait(false);
            }

            return !cancelled && EvalReport.SuitePasses(runs) ? Passed : Failed;
        }
    }

    /// <summary>The world, or <see langword="null"/> after saying why it did not start.</summary>
    /// <remarks>
    /// Only the exception's type and message: they are the host's own startup refusals (a missing descriptor, a schema
    /// it will not migrate), and the eval configures the host with no secret for them to repeat.
    /// </remarks>
    private static async Task<EvalWorld?> BootAsync(EvalOptions options, CancellationToken ct)
    {
        try
        {
            return await EvalWorld.StartAsync(options.RepositoryRoot, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"[eval-assistant] the host did not start: {failure.GetType().Name}: {failure.Message}").ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("[eval-assistant] cancelled before the host started.").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Runs the suite into <paramref name="runs"/>; <see langword="true"/> when it was cancelled part-way.</summary>
    private static async Task<bool> RunSuiteAsync(EvalRunner runner, List<CaseRun> runs, CancellationToken ct)
    {
        try
        {
            await runner.RunAsync(runs, ct).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("[eval-assistant] cancelled; the table below is partial.").ConfigureAwait(false);
            return true;
        }
    }
}
