using MMLib.Alvo.Expressions.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>The one shape of a translation refusal (spec 2026-10-14 §4.3): the construct, where it was written, why, and the fix.</summary>
internal static class TranslationRefusal
{
    internal const string WhenFix = "write the condition as a CEL string — when: \"…\" — or move the check into the handler body.";

    internal const string QueryFix = "build an AlvoQuery with an AlvoFilter yourself, or filter the rows after reading them.";

    internal const string SplitWhenFix = "split the condition into a shorter one, or write it as a CEL string — when: \"…\" — or move the check into the handler body.";

    internal const string SplitQueryFix = "read with fewer terms, filter the rows after reading them, or build an AlvoQuery with an AlvoFilter yourself.";

    /// <summary>A lambda refused for its size alone; the message never prints the tree, which is too large to print.</summary>
    internal static AlvoTranslationException WhenTooLarge(string id, int maxDepth, int maxNodes) =>
        new($"Cannot translate the condition in extension '{id}' (when): {TooLarge(maxDepth, maxNodes)}, more than a condition of {CelParser.MaxSourceLength} characters can hold. Fix: {SplitWhenFix}", SplitWhenFix);

    /// <summary>A predicate refused for its size alone; the message never prints the tree, which is too large to print.</summary>
    internal static AlvoTranslationException QueryTooLarge(string entity, int maxDepth, int maxNodes) =>
        new($"Cannot translate the predicate in a typed query on '{entity}': {TooLarge(maxDepth, maxNodes)}, more than a filter of {AlvoFilter.MaxTerms} terms can hold. Fix: {SplitQueryFix}", SplitQueryFix);

    internal static AlvoTranslationException When(string id, Expression node, string reason, string fix = WhenFix) =>
        new($"Cannot translate '{node}' in extension '{id}' (when): {reason}. Fix: {fix}", fix);

    internal static AlvoTranslationException Query(string entity, Expression node, string reason, string fix = QueryFix) =>
        new($"Cannot translate '{node}' in a typed query on '{entity}': {reason}. Fix: {fix}", fix);

    private static string TooLarge(int maxDepth, int maxNodes) =>
        $"the lambda nests deeper than {maxDepth} levels or holds more than {maxNodes} expression nodes";
}
