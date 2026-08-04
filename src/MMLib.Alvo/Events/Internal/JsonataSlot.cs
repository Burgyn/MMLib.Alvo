using System.Text.RegularExpressions;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Tells a <c>{{…}}</c> template from a raw JSONata expression in a <c>$defs/jsonata</c>-typed slot — the
/// one distinction that decides whether an action's payload is honoured or refused by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in Alvo rather than in the schema.</b> <c>$defs/jsonata</c> is typed <c>string</c>
/// (<c>schema/project.schema.json:398-403</c>) and its own description says <c>{{...}}</c> templates are
/// syntactic sugar, so the schema cannot make the distinction and the apply path must.
/// </para>
/// <para>
/// <b>Why "contains <c>{{</c>" is not the rule.</b> It would classify <c>complex-crm</c>'s
/// <c>"{\"companyIds\": records.id}"</c> as literal text and deliver the JSONata source itself as the
/// webhook body. The no-bare-brace clause catches that, and <c>"$merge([new, {\"source\": \"alvo\"}])"</c>
/// with it.
/// </para>
/// <para>
/// <b>Why "no bare brace" is not the rule either.</b> A JSONata expression need not contain a brace at all:
/// <c>records.id</c> would otherwise be a valid placeholder-free template and deliver the literal string
/// <c>records.id</c>. There is no reason to declare a <em>transform</em> that is a constant, so in a
/// <c>$defs/jsonata</c> slot a placeholder-free string is refused too. Both naive rules fail open, in
/// opposite directions, which is why the rule is a conjunction rather than one clause with decoration.
/// </para>
/// <para>
/// <b>The no-bare-brace clause is load-bearing for <em>injection</em>, not only for classification — whoever
/// implements #149 must not lose it.</b> A <c>payload</c> template renders row text straight into
/// author-written text and <see cref="AlvoTemplate"/> escapes nothing, so this clause is the only reason a row
/// value cannot forge a sibling member: a payload containing <c>{</c> outside a placeholder is refused, so a
/// payload template can never be a JSON <em>object</em> at all. Two things it does not cover, named here rather
/// than assumed away: <c>[</c> and <c>]</c> are not braces, so <c>["{{new.a}}", "{{new.b}}"]</c> is a legal
/// template and a value carrying <c>", "</c> forges array <em>elements</em>; and a bare or quoted string
/// payload becomes <b>invalid</b> JSON — not restructured JSON — when a value carries a quote, a backslash or a
/// newline. Both are malformed-body defects rather than disclosure ones (the receiver is the declared endpoint
/// either way), which is why they are recorded and left to the PR that gives the slot a real evaluator. That
/// PR must produce JSON <em>by construction</em> — serialize a value — and never by interpolating rendered text
/// into author-written text; if it renders text at all, this clause has to survive with it.
/// </para>
/// <para>
/// <b>The asymmetry with the plain-string sugar slots is deliberate</b> and comes from the schema's own
/// typing. In <c>email.to</c>, <c>entity.update.recordId</c>, <c>templates.subject</c>/<c>body</c> and the
/// string values inside <c>entity.update.payload</c> a placeholder-free string <em>is</em> a legitimate
/// literal — a hard-coded address — so those slots accept one and go straight to
/// <see cref="AlvoTemplate.Parse"/> without asking this question at all.
/// </para>
/// </remarks>
internal static partial class JsonataSlot
{
    /// <summary>
    /// Whether <paramref name="source"/> is a template Alvo honours, rather than a raw JSONata expression
    /// this build refuses by name.
    /// </summary>
    /// <param name="source">The string a <c>$defs/jsonata</c>-typed slot carries.</param>
    internal static bool IsTemplate(string source) =>
        !string.IsNullOrEmpty(source) && WellFormedTemplate().IsMatch(source) && ContainsPlaceholder(source);

    private static bool ContainsPlaceholder(string source) =>
        source.Contains(AlvoTemplate.PlaceholderOpen, StringComparison.Ordinal);

    [GeneratedRegex(@"^(?:[^{}]|\{\{[^{}]+\}\})*$")]
    private static partial Regex WellFormedTemplate();
}
