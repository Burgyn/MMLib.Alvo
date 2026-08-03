using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Migrations.Internal;

/// <summary>
/// Why a booting descriptor is older than the one the database is on, written for the operator who has to act
/// on it.
/// </summary>
/// <param name="Headline">The sentence naming the revision this descriptor is and the revision the database is at.</param>
/// <param name="Fixes">The actionable lines, indented the way every other startup refusal indents them.</param>
internal sealed record OutOfOrderBoot(string Headline, IReadOnlyList<string> Fixes);

/// <summary>
/// Where a booting descriptor sits in a project's append-only applied history — the ordering the
/// <see cref="AlvoSchemaStartupMode.Apply"/> default otherwise leaves to a race (#145).
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from history that is already written, not from a counter anybody maintains.</b>
/// <see cref="IDescriptorVersionStore"/> is append-only, so "am I an old pod?" is answerable without a new
/// port member and without asking an author to keep <c>revision</c> monotonic: if this descriptor's content is
/// in the history at a revision older than the current one, it has already had its turn and something newer
/// has since replaced it. A counter-based ordering would give zero protection to the GitOps repository that
/// never bumps the counter — which is most of them.
/// </para>
/// <para>
/// <b>The <em>newest</em> occurrence is what decides.</b> A descriptor re-applied later appears in the history
/// twice — at an older revision and at the current one — and it is current. Searching newest-first and
/// stopping at the first match is both the correct answer and the cheap one.
/// </para>
/// <para>
/// <b>What this cannot see, and what covers it instead.</b> A descriptor Alvo has never applied is not in the
/// history, so it is a forward deploy and applies — which is the behaviour every ordinary deployment depends
/// on, and getting it wrong would brick all of them. An author who does maintain <c>revision</c> gets the
/// declared-revision override for that case, in the one direction that cannot break anybody: see
/// <see cref="DeclaredRevisionSaysOlder"/>.
/// </para>
/// </remarks>
internal static class DescriptorHistoryOrder
{
    /// <summary>Decides whether <paramref name="booting"/> is older than the descriptor the database is on.</summary>
    /// <param name="booting">The descriptor this process is trying to serve.</param>
    /// <param name="bootingJson">
    /// The JSON <paramref name="booting"/> was loaded from, exactly as it was read — compared against the
    /// stored bytes as a fast path, and never as the answer: a reformatted descriptor is the same descriptor.
    /// </param>
    /// <param name="history">
    /// The project's applied history, oldest to newest. That ordering is what makes the last element the
    /// revision the database is on, and it is a <em>contract</em> rather than an assumption about a driver:
    /// <c>DescriptorVersionStoreContractTests.History_is_append_only_and_ordered</c> holds every implementation,
    /// fake and real, to it.
    /// </param>
    /// <returns>Why this boot is out of order, or <see langword="null"/> when it is not.</returns>
    internal static OutOfOrderBoot? Check(
        AlvoDescriptor booting, string bootingJson, IReadOnlyList<DescriptorVersion> history)
    {
        ArgumentNullException.ThrowIfNull(booting);
        ArgumentNullException.ThrowIfNull(bootingJson);
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            return null;
        }

        var current = history[^1];

        return DeclaredRevisionSaysOlder(booting, current)
            ?? HistorySaysOlder(booting, bootingJson, history, current);
    }

    /// <summary>
    /// The declared-<c>revision</c> override: this descriptor says it is generation <em>n</em> and the one the
    /// database is on says it is a later generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It can only ever conclude "older", and both of the things it deliberately does not conclude are
    /// decisions.</b> It cannot conclude "newer": a bumped counter must not wave a descriptor the history calls
    /// older past the gate, or the override becomes the way around the mechanism. And it does not refuse
    /// "equal revision, different content" even though that is a real authoring error — two artifacts claiming
    /// one generation — because a descriptor carrying a decorative <c>revision: 1</c> that nobody ever bumps
    /// would then have its ordinary edit-and-restart loop broken by a field its author never opted into.
    /// </para>
    /// <para>
    /// So the override adds refusals the history would have missed and creates none for a static counter, which
    /// is what makes it safe to honour a field that until now was parsed and read by nothing.
    /// </para>
    /// </remarks>
    /// <param name="booting">The descriptor this process is trying to serve.</param>
    /// <param name="current">The version the database is on.</param>
    private static OutOfOrderBoot? DeclaredRevisionSaysOlder(AlvoDescriptor booting, DescriptorVersion current)
    {
        if (booting.Revision is not { } declared
            || AlvoDescriptor.Parse(current.DescriptorJson).Revision is not { } applied
            || declared >= applied)
        {
            return null;
        }

        return new OutOfOrderBoot(
            $"Alvo cannot start: this process's descriptor declares revision {declared}, and the descriptor "
            + $"applied to this database declares revision {applied}. This process is running an older "
            + "descriptor than the database, so it must not apply its schema over a newer one.",
            DeclaredRevisionFixes(applied));
    }

    /// <summary>
    /// The history comparison: this descriptor's content was applied before, and something else has been
    /// applied since.
    /// </summary>
    /// <param name="booting">The descriptor this process is trying to serve.</param>
    /// <param name="bootingJson">The JSON it was loaded from, for the byte-equality fast path.</param>
    /// <param name="history">The project's applied history, oldest to newest.</param>
    /// <param name="current">The version the database is on.</param>
    private static OutOfOrderBoot? HistorySaysOlder(
        AlvoDescriptor booting,
        string bootingJson,
        IReadOnlyList<DescriptorVersion> history,
        DescriptorVersion current)
    {
        if (NewestRevisionThisDescriptorWasAppliedAs(booting, bootingJson, history) is not { } appliedAs
            || appliedAs >= current.Revision)
        {
            return null;
        }

        return new OutOfOrderBoot(
            "Alvo cannot start: this process's descriptor was already applied to this database as revision "
            + $"{appliedAs}, and the database has since moved on to revision {current.Revision}. This process "
            + "is running an older descriptor than the database, so it must not apply its schema over a newer "
            + "one.",
            HistoryFixes(appliedAs, current.Revision));
    }

    /// <summary>
    /// The newest revision whose descriptor is, canonically, <paramref name="booting"/> — or
    /// <see langword="null"/> when the history has never seen it.
    /// </summary>
    /// <param name="booting">The descriptor this process is trying to serve.</param>
    /// <param name="bootingJson">The JSON it was loaded from, for the byte-equality fast path.</param>
    /// <param name="history">The project's applied history, oldest to newest.</param>
    private static int? NewestRevisionThisDescriptorWasAppliedAs(
        AlvoDescriptor booting, string bootingJson, IReadOnlyList<DescriptorVersion> history)
    {
        var canonical = DescriptorContent.Canonical(booting);

        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (IsSameDescriptor(bootingJson, canonical, history[index].DescriptorJson))
            {
                return history[index].Revision;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a stored descriptor is the one being booted, comparing the stored bytes against the loaded ones
    /// first because a previous boot that recorded the same file recorded it verbatim — and the canonicalization
    /// that hit saves is the whole per-row cost of the history read.
    /// </summary>
    /// <param name="bootingJson">The JSON the booting descriptor was loaded from.</param>
    /// <param name="canonical">The canonical form of the booting descriptor, computed once.</param>
    /// <param name="storedJson">One history row's descriptor JSON.</param>
    private static bool IsSameDescriptor(string bootingJson, string canonical, string storedJson)
        => string.Equals(bootingJson, storedJson, StringComparison.Ordinal)
            || string.Equals(canonical, DescriptorContent.Canonical(storedJson), StringComparison.Ordinal);

    /// <summary>
    /// The ways out of an out-of-order boot diagnosed from the history, and the reason the pod is not dying.
    /// </summary>
    /// <param name="appliedAs">The revision this descriptor was applied as.</param>
    /// <param name="current">The revision the database is on.</param>
    private static IReadOnlyList<string> HistoryFixes(int appliedAs, int current) =>
    [
        $"  Deploy the descriptor this database is on (revision {current}), or roll the schema back to "
            + $"revision {appliedAs} with the migration job that owns it.",
        .. StandingDownFixes,
    ];

    /// <summary>The ways out of an out-of-order boot diagnosed from the declared counter.</summary>
    /// <param name="applied">The revision the applied descriptor declares.</param>
    private static IReadOnlyList<string> DeclaredRevisionFixes(int applied) =>
    [
        $"  Deploy the descriptor the database is on (it declares revision {applied}), or roll the schema "
            + "back with the migration job that owns it.",
        .. StandingDownFixes,
    ];

    /// <summary>
    /// What the two refusals share: how to force the older descriptor through on purpose, and what the process
    /// is doing meanwhile.
    /// </summary>
    /// <remarks>
    /// The force-it-through line says <em>and clear the destructive gate</em> rather than stopping at the
    /// counter, because standing an old pod down does not make a rollback appliable — the plan back is still a
    /// drop, and deviation 57's gate still refuses it. An operator told only half of that would bump the
    /// counter, meet a second refusal they were never warned about, and reasonably conclude the first message
    /// lied.
    /// </remarks>
    private static IEnumerable<string> StandingDownFixes
    {
        get
        {
            yield return "  To deploy this older descriptor deliberately, bump its revision above the "
                + $"applied one and set {AlvoSchemaOptions.AllowDestructiveEnvironmentVariable}=true if the "
                + "plan back discards data.";
            yield return "  This process is not exiting: it reports not ready and serves nothing, so an "
                + "orchestrator drains it rather than routing to it.";
        }
    }
}
