using System.Globalization;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>The table the maintainer publishes into <c>docs/assistant-evals.md</c>, and the suite's verdict.</summary>
/// <remarks>
/// The suite passes when every case in every language passes at least two thirds of its runs and the whole suite at
/// least <see cref="OverallFloor"/> — the reliability design's §4.2 bar.
/// </remarks>
internal static class EvalReport
{
    private const double OverallFloor = 0.90;

    internal static bool SuitePasses(IReadOnlyList<CaseRun> runs) =>
        runs.Count > 0
        && Groups(runs).All(group => group.Count(run => run.Verdict.Passed) * 3 >= group.Count() * 2)
        && PassRate(runs) >= OverallFloor;

    /// <summary>Prints the table and the suite's verdict; a cancelled run says so, and is never a pass.</summary>
    /// <param name="options">The run's options.</param>
    /// <param name="runs">Every turn that finished.</param>
    /// <param name="output">Where to print.</param>
    /// <param name="cancelled">Whether the run was stopped before the suite finished.</param>
    internal static void Print(EvalOptions options, IReadOnlyList<CaseRun> runs, TextWriter output, bool cancelled = false)
    {
        var partial = cancelled ? string.Create(CultureInfo.InvariantCulture, $" — CANCELLED after {runs.Count} turn(s), partial") : string.Empty;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Model: {options.Connection.Model} @ {options.Connection.Endpoint.Host} — runs per case: {options.Runs}{partial}"));
        output.WriteLine();
        output.WriteLine("| case | lang | pass | median tool calls | median requests | p50 latency | median tokens |");
        output.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var group in Groups(runs))
        {
            output.WriteLine(Row(group));
        }

        output.WriteLine();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Overall: {runs.Count(run => run.Verdict.Passed)}/{runs.Count} ({PassRate(runs):P1}) — {(!cancelled && SuitePasses(runs) ? "PASS" : "FAIL")}"));
    }

    private static IEnumerable<IGrouping<(string Case, string Language), CaseRun>> Groups(IReadOnlyList<CaseRun> runs) =>
        runs.GroupBy(run => (run.Case, run.Language));

    private static string Row(IGrouping<(string Case, string Language), CaseRun> group) => string.Create(
        CultureInfo.InvariantCulture,
        $"| {group.Key.Case} | {group.Key.Language} | {group.Count(run => run.Verdict.Passed)}/{group.Count()} | "
        + $"{Median(group.Select(run => (double)run.Turn.ToolCalls.Count))} | "
        + $"{Median(group.Select(run => (double)run.Turn.Requests))} | "
        + $"{Median(group.Select(run => run.Turn.Elapsed.TotalSeconds)):0.0}s | {Median(group.Select(run => (double)run.Turn.Tokens))} |");

    private static double PassRate(IReadOnlyList<CaseRun> runs) =>
        runs.Count == 0 ? 0 : runs.Count(run => run.Verdict.Passed) / (double)runs.Count;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
