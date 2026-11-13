namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>How one CEL slot spells a reference to the field being renamed.</summary>
/// <remarks>
/// <b>Three spellings, from <c>docs/architecture/relation-rules.md</c>.</b> In its own entity's slots a field is a bare
/// name or a member of a row image (<c>new.status</c>). In a rule or a lookup check it may also be read through a ref
/// to its entity (<c>work_order_id.assigned_to</c>) — the ref is then a prefix — and a ref field is itself the head of
/// such a path. Another entity's relation slot reads a bare name as its own column, never this one.
/// </remarks>
/// <param name="Bare">Whether a bare name is the field.</param>
/// <param name="Prefixes">The names whose member the field is: the row images, and the refs to its entity.</param>
/// <param name="Head">Whether the field is a ref that heads a relation path (<c>&lt;ref&gt;.&lt;field&gt;</c>).</param>
internal sealed record CelSpelling(bool Bare, IReadOnlyCollection<string> Prefixes, bool Head)
{
    /// <summary>The row images a hook, a transition or an automation reads.</summary>
    public static IReadOnlyList<string> Images { get; } = ["new", "old"];

    /// <summary>An entity's own slot outside its relations: a bare name, <c>new.</c> or <c>old.</c>.</summary>
    public static CelSpelling Own { get; } = new(Bare: true, Images, Head: false);
}
