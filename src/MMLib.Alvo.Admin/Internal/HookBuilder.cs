using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// What the hook editor holds, and the action it builds from it in the schema's own shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>A before-hook and an after-hook are not the same editor.</b> The frozen schema's
/// <c>$defs/beforeHookList</c> admits <c>reject</c> and <c>mutate</c> only — a before-hook runs inside the
/// write's own transaction, with no network — and an after-hook is where a webhook or an email belongs. The
/// kinds a point admits are therefore decided by the point, and switching the point moves the kind with it.
/// </para>
/// <para>
/// <b>Out of the component so it can be tested</b> (docs/architecture/admin-dashboard-review.md, F-13).
/// </para>
/// </remarks>
internal sealed class HookBuilder
{
    /// <summary>The kind that refuses the write.</summary>
    public const string Reject = "reject";

    /// <summary>The kind that patches the row.</summary>
    public const string Mutate = "mutate";

    /// <summary>The kind that delivers to a declared endpoint.</summary>
    public const string Webhook = "webhook";

    /// <summary>The kind that sends a templated email.</summary>
    public const string Email = "email";

    private static readonly string[] _beforeKinds = [Reject, Mutate];
    private static readonly string[] _deleteKinds = [Reject];
    private static readonly string[] _afterKinds = [Webhook, Email];

    /// <summary>The six hook points, in the order a write passes them.</summary>
    public static IReadOnlyList<string> Points { get; } =
        ["beforeCreate", "beforeUpdate", "beforeDelete", "afterCreate", "afterUpdate", "afterDelete"];

    /// <summary>The point the hook is declared at.</summary>
    public string Point { get; private set; } = Points[0];

    /// <summary>The action's kind; one of <see cref="Kinds"/>.</summary>
    public string Kind { get; set; } = Reject;

    /// <summary>The CEL guard, or empty for a hook that always runs.</summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>A reject's message, which the caller reads as the problem's detail.</summary>
    public string RejectMessage { get; set; } = string.Empty;

    /// <summary>The field a mutate patches.</summary>
    public string MutateField { get; set; } = string.Empty;

    /// <summary>The CEL a mutate sets it to.</summary>
    public string MutateValue { get; set; } = string.Empty;

    /// <summary>A webhook's endpoint, by its declared name.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>An email's template.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>Who an email goes to.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// The action kinds this point admits.
    /// </summary>
    /// <remarks>
    /// <b>Three answers, not two, and the third does not come from the schema.</b> <c>$defs/beforeHookList</c>
    /// is one definition for all three before-points, so the schema alone would offer <c>mutate</c> at
    /// <c>beforeDelete</c> — which <c>BeforeHookCompiler</c> refuses outright, because the row is being
    /// removed and a patch against it is a slot that compiles cleanly and is then discarded.
    /// </remarks>
    public IReadOnlyList<string> Kinds => Point switch
    {
        "beforeDelete" => _deleteKinds,
        _ => IsBefore(Point) ? _beforeKinds : _afterKinds,
    };

    /// <summary>
    /// The row images a condition at this point may name, as markup.
    /// </summary>
    /// <remarks>
    /// <b>Guidance that named both at every point was guidance to write a refused descriptor.</b> A create
    /// has no pre-image and a delete produces no post-image, so <c>old.</c> under a <c>beforeCreate</c> and
    /// <c>new.</c> under a <c>beforeDelete</c> are references the phase cannot answer — and
    /// <c>BeforeHookCompiler</c> refuses both, because an unanswerable reference resolves to null and the
    /// null rule collapses every comparison against it, including <c>!=</c>.
    /// </remarks>
    public string Images => Point switch
    {
        "beforeCreate" => "<code class=\"a-mono\">new</code>",
        "beforeDelete" => "<code class=\"a-mono\">old</code>",
        _ => "<code class=\"a-mono\">new</code>, <code class=\"a-mono\">old</code>",
    };

    /// <summary>A condition that is valid at this point, for the placeholder.</summary>
    public string Example => Point switch
    {
        "beforeCreate" => "new.priority == 'high'",
        "beforeDelete" => "old.status == 'completed'",
        _ => "new.status == 'completed'",
    };

    /// <summary>Whether a point runs inside the write's transaction.</summary>
    public static bool IsBefore(string point)
        => point.StartsWith("before", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Switches the point, and the action kind with it when the current one no longer fits.
    /// </summary>
    /// <remarks>
    /// Moving from <c>afterCreate</c> to <c>beforeCreate</c> with <c>webhook</c> still selected would leave
    /// the editor holding a descriptor the apply refuses — a before-hook may not touch the network — and the
    /// refusal would name a choice the operator never made.
    /// </remarks>
    public void Choose(string point)
    {
        Point = point;
        if (!Kinds.Contains(Kind))
        {
            Kind = Kinds[0];
        }
    }

    /// <summary>
    /// The action in the schema's shape, or why it cannot be built.
    /// </summary>
    /// <remarks>
    /// One refusal per action kind, and each is the schema's own <c>required</c>. Everything past that —
    /// whether the CEL compiles, whether the endpoint or the template exists — is the apply's, which answers
    /// it against the whole descriptor rather than against this one control.
    /// </remarks>
    /// <param name="refusal">Why the action cannot be built, when it cannot.</param>
    /// <returns>The action, or <see langword="null"/> with <paramref name="refusal"/> set.</returns>
    public JsonObject? Build(out string? refusal)
    {
        refusal = Missing();
        return refusal is null ? Action() : null;
    }

    /// <summary>Empties what was typed, and keeps the point and the kind for the next hook.</summary>
    public void Clear()
    {
        Condition = string.Empty;
        RejectMessage = string.Empty;
        MutateField = string.Empty;
        MutateValue = string.Empty;
        Endpoint = string.Empty;
        Template = string.Empty;
        To = string.Empty;
    }

    private string? Missing() => Kind switch
    {
        Reject when Blank(RejectMessage) => "A reject carries the message the caller reads. Write one.",
        Mutate when Blank(MutateField) || Blank(MutateValue)
            => "A mutate patches at least one field. Name the field and what to set it to.",
        Webhook when Blank(Endpoint) => "A webhook names an endpoint declared under 'webhooks.endpoints'.",
        Email when Blank(Template) || Blank(To) => "An email names a template and who it goes to.",
        _ => null,
    };

    private JsonObject Action() => Kind switch
    {
        Reject => new JsonObject { [Reject] = RejectMessage },
        Mutate => new JsonObject
        {
            [Mutate] = new JsonObject { [MutateField] = new JsonObject { ["$cel"] = MutateValue } },
        },
        Webhook => new JsonObject { ["type"] = Webhook, ["endpoint"] = Endpoint },
        _ => new JsonObject { ["type"] = Email, ["template"] = Template, ["to"] = To },
    };

    private static bool Blank(string value) => string.IsNullOrWhiteSpace(value);
}
