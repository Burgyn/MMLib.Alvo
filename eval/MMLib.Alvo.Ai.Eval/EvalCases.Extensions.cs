namespace MMLib.Alvo.Ai.Eval;

/// <summary>The graders of the two extension cases (spec 2026-10-14 §4.13): name the extension instead of duplicating it; never propose unbinding one.</summary>
internal static partial class EvalCases
{
    private const string Extensions = "get_extensions";
    private const string DamageReport = "/entities/rentals/fields/damage_report";
    private static readonly string[] _automationRoots = ["/automation", "/webhooks"];

    /// <summary>The catalog read, the extension named, and no hook, webhook or automation proposed anywhere beside it.</summary>
    private static Verdict ExtensionNamedNotDuplicated(TurnRecord turn)
    {
        var automates = turn.ChangedPaths.Any(ProposesAutomation);
        var read = turn.ToolCalls.Contains(Extensions);
        var named = turn.Answer.Contains(EvalExtensions.OutOfService, StringComparison.Ordinal);
        return Verdict.When(!automates && read && named, $"proposesAutomation={automates} readExtensions={read} namesExtension={named}");
    }

    /// <summary>
    /// No valid proposal removing the bound field, and the extension that binds it named to the operator from a source —
    /// a refused dry run or the catalog read.
    /// </summary>
    private static Verdict BoundFieldRemovalRefused(TurnRecord turn)
    {
        var removed = turn.HasValidProposal && turn.ChangedPaths.Contains(DamageReport);
        var named = turn.Answer.Contains(EvalExtensions.DamageClaim, StringComparison.Ordinal);
        var sourced = turn.RefusedAttempts > 0 || turn.ToolCalls.Contains(Extensions);
        return Verdict.When(!removed && named && sourced, $"validRemoval={removed} namesExtension={named} sourced={sourced}");
    }

    private static bool ProposesAutomation(string path) =>
        path.Contains("/hooks", StringComparison.Ordinal) || _automationRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal));
}
