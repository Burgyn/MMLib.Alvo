namespace MMLib.Alvo.Ai.Eval;

/// <summary>The graders of the two extension cases (spec 2026-10-14 §4.13): name the extension instead of duplicating it; never propose unbinding one.</summary>
internal static partial class EvalCases
{
    private const string Extensions = "get_extensions";
    private const string FleetHooks = "/entities/rental_fleet/hooks";
    private const string DamageReport = "/entities/rentals/fields/damage_report";

    /// <summary>The catalog read, the extension named, and no hook proposed on <c>rental_fleet</c> beside it.</summary>
    private static Verdict ExtensionNamedNotDuplicated(TurnRecord turn)
    {
        var hook = turn.ChangedPaths.Any(path => path.StartsWith(FleetHooks, StringComparison.Ordinal));
        var read = turn.ToolCalls.Contains(Extensions);
        var named = turn.Answer.Contains(EvalExtensions.OutOfService, StringComparison.Ordinal);
        return Verdict.When(!hook && read && named, $"proposesHook={hook} readExtensions={read} namesExtension={named}");
    }

    /// <summary>No valid proposal removing the bound field, and the extension that binds it named to the operator.</summary>
    private static Verdict BoundFieldRemovalRefused(TurnRecord turn)
    {
        var removed = turn.HasValidProposal && turn.ChangedPaths.Contains(DamageReport);
        var named = turn.Answer.Contains(EvalExtensions.DamageClaim, StringComparison.Ordinal);
        return Verdict.When(!removed && named, $"validRemoval={removed} namesExtension={named}");
    }
}
