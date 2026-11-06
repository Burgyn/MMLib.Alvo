using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The wire text of every refusal a relation catalog gives: a member that is no reference, a target a rule cannot
/// read, a child that is ambiguous, and a receiver that is no child. Positions are the type checker's to set.
/// </summary>
internal static class RelationRefusals
{
    /// <summary>The built-in users entity, which a rule compares a ref with rather than reads.</summary>
    internal const string UsersEntity = "users";

    /// <summary>Why a <c>storage: dynamic</c> entity cannot be read.</summary>
    internal const string Dynamic = "dynamic (#41)";

    internal const string BuiltInUsers = "the built-in users entity";

    internal const string Undeclared = "not declared";

    internal static CelCompilationError NotAReference(string entity, string member, string field, IEnumerable<string> refs) => new(
        $"'{member}' is not a reference on '{entity}', so '{member}.{field}' has nothing to follow.",
        $"Follow a 'ref' field of '{entity}' ({List(refs)}), or reach a child with <child>.exists(x, …).",
        0);

    internal static CelCompilationError Unreadable(string target, string reason, string reference) => new(
        $"'{target}' is {reason}, so a rule cannot read its rows.",
        $"Compare the ref itself, e.g. '{reference} == @user.id'.",
        0);

    internal static CelCompilationError Ambiguous(string parent, string child, IReadOnlyList<string> references) => new(
        $"'{parent}' reaches '{child}', which references '{parent}' more than once, so which relationship to follow is "
        + $"ambiguous. {RollupResolver.Alternatives(child, parent, references)}",
        "Name the reference: "
        + string.Join(" or ", references.Order(StringComparer.Ordinal).Select(field => $"{child}_via_{field}.exists(…)")) + ".",
        0);

    internal static CelCompilationError NotAChild(string parent, string receiver, IEnumerable<string> children) => new(
        $"'{receiver}' does not reference '{parent}', so '{receiver}.exists(…)' has no rows to read.",
        $"Reach a child that references '{parent}' ({List(children)}), or follow a 'ref' field with <ref>.<field>.",
        0);

    internal static CelCompilationError RefNotChild(string parent, string receiver) => new(
        $"'{receiver}' is a reference of '{parent}', not a child, so '{receiver}.exists(…)' has no rows to read.",
        $"Follow the reference with {receiver}.<field>.",
        0);

    internal static CelCompilationError UnreadableChild(string parent, string receiver, string reason, IEnumerable<string> children) =>
        NotAChild(parent, receiver, children) with { Message = $"'{receiver}' is {reason}, so a rule cannot read its rows." };

    private static string List(IEnumerable<string> names) =>
        names.Order(StringComparer.Ordinal).ToList() is { Count: > 0 } ordered ? string.Join(", ", ordered) : "none";
}
