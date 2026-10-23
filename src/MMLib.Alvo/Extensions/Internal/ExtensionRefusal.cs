using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>The one start-up refusal listing every extension problem (spec 2026-10-14 §4.2: all problems at once).</summary>
internal static class ExtensionRefusal
{
    internal static AlvoStartupRefusedException Of(IReadOnlyList<ExtensionProblem> problems) => new(
        $"Alvo cannot start: {problems.Count} problem(s) in the extensions this host registers, all listed here:"
        + string.Concat(problems.Select(problem => $"{Environment.NewLine}- '{problem.Id}' ({problem.Kind}) at {problem.Locator}: {problem.Message}")),
        string.Join(Environment.NewLine, problems.Select(problem => $"'{problem.Id}': {problem.Fix}")));
}
