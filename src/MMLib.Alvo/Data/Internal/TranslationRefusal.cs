using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>The one shape of a translation refusal (spec 2026-10-14 §4.3): the construct, where it was written, why, and the fix.</summary>
internal static class TranslationRefusal
{
    internal const string WhenFix = "write the condition as a CEL string — when: \"…\" — or move the check into the handler body.";

    internal const string QueryFix = "build an AlvoQuery with an AlvoFilter yourself, or filter the rows after reading them.";

    internal static AlvoTranslationException When(string id, Expression node, string reason, string fix = WhenFix) =>
        new($"Cannot translate '{node}' in extension '{id}' (when): {reason}. Fix: {fix}", fix);

    internal static AlvoTranslationException Query(string entity, Expression node, string reason, string fix = QueryFix) =>
        new($"Cannot translate '{node}' in a typed query on '{entity}': {reason}. Fix: {fix}", fix);
}
