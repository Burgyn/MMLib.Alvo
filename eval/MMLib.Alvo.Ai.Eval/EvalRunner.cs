using MMLib.Alvo.Ai.Internal;

using System.Diagnostics;
using System.Globalization;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Runs every case, in every language, the requested number of times, and grades each turn.</summary>
/// <param name="world">The running host every turn reads.</param>
/// <param name="options">What was asked for.</param>
/// <param name="trace">Where each graded turn is written in full, or <see langword="null"/> for nowhere.</param>
internal sealed class EvalRunner(EvalWorld world, EvalOptions options, EvalTrace? trace)
{
    private const int MaximumToolCalls = 6;

    /// <summary>Runs the suite, adding each graded turn to <paramref name="runs"/> as it finishes.</summary>
    /// <remarks>The caller owns the list, so a cancelled run still has every turn that finished to report.</remarks>
    /// <param name="runs">Where the graded turns go.</param>
    /// <param name="ct">A token to cancel the run.</param>
    internal async Task RunAsync(List<CaseRun> runs, CancellationToken ct)
    {
        foreach (var evalCase in EvalCases.All.Where(candidate => options.Case is null || candidate.Name == options.Case))
        {
            foreach (var language in options.Languages)
            {
                for (var run = 1; run <= options.Runs; run++)
                {
                    var graded = await RunOnceAsync(evalCase, language, ct).ConfigureAwait(false);
                    runs.Add(graded);
                    trace?.Write(graded);
                    await Console.Error.WriteLineAsync($"[eval-assistant] {graded.Line}").ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>What every turn must hold whatever it was asked: it answered, it stayed in bounds, it patched.</summary>
    /// <remarks>
    /// <para>
    /// Tool rounds are graded against <see cref="AlvoAssistant.MaximumIterations"/>, not model requests: the cap bounds
    /// how often tools are invoked, and the loop may ask once more for the answer after the last round.
    /// </para>
    /// <para>
    /// <b>The ≤ 12 term cannot fail today</b>, and is kept anyway: every round carries at least one call, so the ≤ 6
    /// tool-call bar is met first. It is the spec's invariant (§4.2) stated where a later change to either bound would
    /// be read, and <c>requests=</c> is printed beside it so a loop that ended at the cap is visible.
    /// </para>
    /// </remarks>
    internal static Verdict Invariants(TurnRecord turn)
    {
        if (turn.Updates.OfType<AssistantUpdate.Failed>().FirstOrDefault() is { } failed)
        {
            return new Verdict(false, $"turn failed: {failed.Reason} (provider status: {turn.ProviderStatus ?? ProviderStatusLogger.NoResponse})");
        }

        var wholeDocument = turn.HasViolation("code", JsonPatchError.WholeDocumentReplace);
        return Verdict.When(
            turn.ToolRounds <= AlvoAssistant.MaximumIterations && turn.ToolCalls.Count <= MaximumToolCalls && !wholeDocument,
            $"requests={turn.Requests} toolRounds={turn.ToolRounds} toolCalls={turn.ToolCalls.Count} wholeDocument={wholeDocument}");
    }

    /// <summary>The invariants first; a turn that holds them is graded by its case, with both diagnostics kept.</summary>
    internal static Verdict Graded(EvalCase evalCase, TurnRecord turn)
    {
        var invariants = Invariants(turn);
        if (!invariants.Passed)
        {
            return invariants;
        }

        var verdict = evalCase.Grade(turn);
        return verdict with { Why = $"{verdict.Why} | {invariants.Why}" };
    }

    private async Task<CaseRun> RunOnceAsync(EvalCase evalCase, string language, CancellationToken ct)
    {
        var turn = await AskAsync(evalCase.Prompt(language), ct).ConfigureAwait(false);
        return new CaseRun(evalCase.Name, language, turn, Graded(evalCase, turn));
    }

    private async Task<TurnRecord> AskAsync(string prompt, CancellationToken ct)
    {
        RecordingChatClient? recorder = null;
        var logger = new ProviderStatusLogger();
        var assistant = new AlvoAssistant(
            world.Management,
            new FixedConnection(options.Connection),
            connection => recorder = new RecordingChatClient(ChatClientFactory.For(connection)),
            logger);

        world.ActAsAdministrator();
        var original = await world.Management.GetDescriptorAsync(EvalWorld.Project, ct).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var updates = new List<AssistantUpdate>();
        await foreach (var update in assistant.AskAsync(new AssistantRequest(EvalWorld.Project, prompt, []), ct).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return new TurnRecord(
            original.DescriptorJson, updates, clock.Elapsed,
            recorder?.Requests ?? 0, recorder?.ToolRounds ?? 0, recorder?.Tokens ?? 0, recorder?.Calls ?? [], logger.Status);
    }

    /// <summary>The one connection the eval measures, resolved the way a configured deployment resolves it.</summary>
    private sealed class FixedConnection(AlvoAiConnection connection) : IAiConnectionResolver
    {
        public ValueTask<AiConnectionResolution> ResolveAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new AiConnectionResolution(
                connection, AiConnectionSource.Configuration, connection.ApiKey is null ? AiKeyState.NotNeeded : AiKeyState.Present));
    }
}

/// <summary>One graded turn of one case in one language.</summary>
/// <param name="Case">The case's name.</param>
/// <param name="Language">The language it was asked in.</param>
/// <param name="Turn">What the turn produced.</param>
/// <param name="Verdict">How it was graded.</param>
internal sealed record CaseRun(string Case, string Language, TurnRecord Turn, Verdict Verdict)
{
    internal string Line => string.Create(
        CultureInfo.InvariantCulture,
        $"{Case} [{Language}] {(Verdict.Passed ? "PASS" : "FAIL")} {Verdict.Why} ({Turn.ToolCalls.Count} calls, {Turn.Elapsed.TotalSeconds:0.0}s)");
}
