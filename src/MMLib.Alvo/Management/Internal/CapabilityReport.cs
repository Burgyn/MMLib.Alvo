using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Projects the framework's two "not yet" tables into the capabilities payload — <b>and rewrites
/// neither</b>.
/// </summary>
/// <remarks>
/// <para>
/// The consequence that makes this worth an endpoint: when the PR that implements automation deletes the
/// entry from <see cref="UnhonouredSubsystems"/>, the badge disappears from the dashboard <em>without
/// anyone touching the dashboard</em>.
/// </para>
/// <para>
/// <b>Two classes, and a client must not make them look alike.</b> A <em>warned</em> block applies and then
/// does nothing; a <em>refused</em> feature is rejected at apply, so a control for it is worse than a
/// missing control — its only possible output is a descriptor the apply will reject.
/// </para>
/// <para>
/// <b>Named for what it produces rather than for the model it produces.</b> Calling it
/// <c>ManagementCapabilities</c> would collide with the public record of that name in the parent namespace
/// and force every reference on both sides to be qualified, which is a cost paid forever to save a word
/// once.
/// </para>
/// <para>
/// <b>There is no <c>issue</c> field, and that is a decision.</b> The F5 design's sketch shows one;
/// <see cref="UnhonouredSubsystem"/> carries <c>Block</c>, <c>IsDeclaredBy</c> and <c>Consequence</c> and
/// nothing else. Some consequences name an issue inside the prose and some name none, so minting a number
/// here would be inventing data — the prose is served as written and carries whatever its author put in it.
/// </para>
/// <para>
/// <b><c>warned</c> carries qualified slots as well as top-level blocks</b> (§8d items 21 and 27):
/// <see cref="UnhonouredSubsystems.WithinBlocks"/>' <c>auth.providers</c> and <c>entity.storage</c>, and
/// <see cref="UnhonouredSubsystems.ReportedOnly"/>' <c>entity.realtime</c>, after the blocks, spelled as
/// <c>refused</c>'s slots already are (<c>field.default</c>). A client keyed on a top-level block name matches
/// none of them, so no existing badge moves. <c>ManagementWarnedBlock.Block</c>'s own summary still says "top-level
/// block name"; correcting that sentence is an Abstractions change, left to the batch that grows Abstractions
/// (B7), and this paragraph is where the wider meaning is stated until then.
/// </para>
/// <para>
/// <b>Deliberate deviation: the report and the apply warning are no longer the same list.</b> Until §8d item 27
/// the warned half was exactly what the apply warns about. <c>entity.realtime</c> is now reported and never
/// warned, because its schema default is <c>true</c> — a line at apply would fire on every descriptor ever
/// applied (the reasoning is <see cref="UnhonouredSubsystems"/>' and <c>docs/architecture/data-api.md</c>'s, #38)
/// — while a dashboard that says nothing about it lets an operator believe changes are published. The triage's
/// alternative, a dashboard note citing #38 by link, would have put a sentence about the build in a client, which
/// is the second spelling this type exists to prevent.
/// </para>
/// </remarks>
internal static class CapabilityReport
{
    /// <summary>The top-level descriptor blocks this build does honour.</summary>
    /// <remarks>
    /// <para>
    /// <b>Written, not derived, and that is stated rather than hidden.</b> Nothing in the framework
    /// enumerates what it <em>does</em> honour — the two tables enumerate only what it does not. What can
    /// actually go wrong is this list and <see cref="UnhonouredSubsystems.All"/> naming the same block, and
    /// a fact holds the two disjoint; a second fact holds every name here to being a block
    /// <c>schema/project.schema.json</c> actually declares, because a name no descriptor can carry reads as
    /// coverage while describing nothing.
    /// </para>
    /// <para>
    /// <b>The two lists together are deliberately not the whole schema.</b> <c>branding</c> is in neither:
    /// it left <see cref="UnhonouredSubsystems"/> on the argument that an author who writes it and sees no
    /// logo has looked and found out, and nothing renders it yet either — so claiming it here would be the
    /// lie this type exists to prevent. The descriptor's metadata keys (<c>name</c>, <c>revision</c>,
    /// <c>apiVersion</c>, <c>description</c>) are not subsystems and are in neither list for that reason.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> Honoured { get; } =
        ["entities", "auth", "tenancy", "access", "formats"];

    /// <summary>What this build honours, warns about, and refuses.</summary>
    internal static ManagementCapabilities Project() => new(
        Honoured,
        [.. UnhonouredSubsystems.All
            .Concat(UnhonouredSubsystems.WithinBlocks)
            .Concat(UnhonouredSubsystems.ReportedOnly)
            .Select(block => new ManagementWarnedBlock(block.Block, block.Consequence))],
        [.. UnhonouredFeatures.EveryRefusal.Select(refusal =>
            new ManagementRefusedFeature(refusal.Slot, refusal.Consequence, refusal.Fix))]);
}
