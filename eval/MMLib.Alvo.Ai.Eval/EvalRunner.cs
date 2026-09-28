using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Ai.Internal;

using System.Diagnostics;
using System.Globalization;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Runs every case, in every language, the requested number of times, and grades each turn.</summary>
/// <param name="world">The running host every turn reads.</param>
/// <param name="options">What was asked for.</param>
internal sealed class EvalRunner(EvalWorld world, EvalOptions options)
{
    private const int MaximumToolCalls = 6;

    internal async Task<IReadOnlyList<CaseRun>> RunAsync(CancellationToken ct)
    {
        var runs = new List<CaseRun>();
        foreach (var evalCase in EvalCases.All.Where(candidate => options.Case is null || candidate.Name == options.Case))
        {
            foreach (var language in options.Languages)
            {
                for (var run = 1; run <= options.Runs; run++)
                {
                    runs.Add(await RunOnceAsync(evalCase, language, ct).ConfigureAwait(false));
                    await Console.Error.WriteLineAsync($"[eval-assistant] {runs[^1].Line}").ConfigureAwait(false);
                }
            }
        }

        return runs;
    }

    /// <summary>What every turn must hold whatever it was asked: it answered, it stayed in bounds, it patched.</summary>
    /// <remarks>
    /// Tool rounds are graded against <see cref="AlvoAssistant.MaximumIterations"/>, not model requests: the cap bounds
    /// how often tools are invoked, and the loop may ask once more for the answer after the last round.
    /// </remarks>
    private static Verdict Invariants(TurnRecord turn)
    {
        if (turn.Updates.OfType<AssistantUpdate.Failed>().FirstOrDefault() is { } failed)
        {
            return new Verdict(false, $"turn failed: {failed.Reason}");
        }

        var wholeDocument = turn.HasViolation("code", JsonPatchError.WholeDocumentReplace);
        return Verdict.When(
            turn.ToolRounds <= AlvoAssistant.MaximumIterations && turn.ToolCalls.Count <= MaximumToolCalls && !wholeDocument,
            $"toolRounds={turn.ToolRounds} toolCalls={turn.ToolCalls.Count} wholeDocument={wholeDocument}");
    }

    private async Task<CaseRun> RunOnceAsync(EvalCase evalCase, string language, CancellationToken ct)
    {
        var turn = await AskAsync(evalCase.Prompt(language), ct).ConfigureAwait(false);
        var verdict = Invariants(turn) is { Passed: false } broken ? broken : evalCase.Grade(turn);

        return new CaseRun(evalCase.Name, language, turn, verdict);
    }

    private async Task<TurnRecord> AskAsync(string prompt, CancellationToken ct)
    {
        RecordingChatClient? recorder = null;
        var assistant = new AlvoAssistant(
            world.Management,
            new FixedConnection(options.Connection),
            connection => recorder = new RecordingChatClient(ChatClientFactory.For(connection)),
            NullLogger<AlvoAssistant>.Instance);

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
            recorder?.Requests ?? 0, recorder?.ToolRounds ?? 0, recorder?.Tokens ?? 0, recorder?.Calls ?? []);
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
        $"{Case} [{Language}] {(Verdict.Passed ? "PASS" : "FAIL " + Verdict.Why)} ({Turn.ToolCalls.Count} calls, {Turn.Elapsed.TotalSeconds:0.0}s)");
}
