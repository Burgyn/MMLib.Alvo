using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// <b>The one authority on the top-level descriptor blocks this build parses and then honours nowhere.</b>
/// A descriptor declaring one of these applies successfully and is warned about, by name, once — which is
/// the whole difference between this table and <see cref="UnhonouredFeatures"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Once, at apply" means the BOOT apply, and that is a gap rather than a wording nicety.</b> The only
/// caller of <see cref="Warn"/> is <c>DescriptorBootPlan.LoadAsync</c>; <c>RuntimeSchemaService.ApplyAsync</c>
/// — the dashboard-first / Management-API path — calls it nowhere, so a descriptor applied at runtime earns
/// no line at all. That is pre-existing and true of every entry here, and it matters most for the newest
/// one: an operator applying through a dashboard is the reader least likely to be watching boot logs, and
/// <c>access</c> is the entry whose absence they cannot otherwise notice. Tracked on #83, whose scope is
/// exactly "nothing primes, and nothing warns, at startup in runtime-apply mode".
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// <b>Warned, not refused, and the line is a rule rather than a case-by-case judgement.</b>
/// <see cref="UnhonouredFeatures"/> refuses what <em>silently produces wrong data</em>: an ignored
/// <c>default</c> stores NULL where a value was expected, and no author can see that from the outside.
/// These blocks fail the other way — an author who declared a webhook and never receives one, or an
/// automation rule that never fires, observes the absence directly. Refusing them would refuse a
/// descriptor whose only defect is being ahead of the implementation, and the descriptor is meant to be
/// the durable artifact that outlives any one build.
/// </para>
/// <para>
/// <b>Why a warning at all, then.</b> "Observable" is not the same as "observed promptly". A webhook that
/// never fires looks exactly like a webhook whose endpoint is down, and an automation that never runs looks
/// like a condition that never matched — so the author spends the debugging budget on the wrong layer. One
/// line at apply, naming the blocks, converts that into a fact they already have.
/// </para>
/// <para>
/// <b>Every entry names a real top-level property of the frozen schema</b>, and that is asserted from
/// outside this table rather than trusted:
/// <c>UnhonouredSubsystemsTests.Every_unhonoured_subsystem_names_a_block_the_schema_declares</c> reads
/// <c>schema/project.schema.json</c>. The check is not ceremonial — it is what caught this table's first
/// draft, which carried <c>realtime</c> (a block the schema does not declare at all, so the entry would
/// have warned about nothing forever) and spelled <c>automation</c> as <c>automations</c> (the same defect
/// one letter smaller). An entry matching nothing is worse than a missing entry, because it reads as
/// coverage.
/// </para>
/// <para>
/// <b>A block declined by value is not a declaration</b>, the same rule
/// <c>DescriptorValidatorTests.A_feature_declined_by_value_is_not_a_declaration</c> holds for
/// <c>softDelete: false</c>: <c>"dynamicEntities": { "enabled": false }</c> and an empty
/// <c>"automation": {}</c> are an author saying they are not using the feature, and warning them about it
/// is a line they cannot act on.
/// </para>
/// <para>
/// <b>An entry is earned two ways, and it is worth saying so rather than pretending there is one rule.</b>
/// Limb one is the original: the absence is observable but easy to misattribute — a webhook that never
/// fires looks like an endpoint that is down. Limb two is a block whose <em>name</em> promises something it
/// does not do, where the author's mistake is not an unmet expectation but a <em>false belief</em>. That
/// limb is not new here: the <c>webhooks</c> entry already rests on it — <em>"an unsigned delivery an
/// author believes is signed is a security absence"</em> — and it is why that entry names <c>secretRef</c>
/// specifically.
/// </para>
/// <para>
/// <b><c>access</c> is on the table under limb two; <c>branding</c> is on neither and stays out.</b> An
/// earlier version of this paragraph excluded the pair together, on limb one alone: both are parsed and
/// consumed by no product code, both describe an admin-dashboard surface this build has no trace of, so
/// neither absence could be observed and neither warning could name a disappointment the author can yet
/// have. That reasoning holds for <c>branding</c> — an author who writes it and sees no logo has looked and
/// found out, and it is harmless — and fails for <c>access</c> (#146), whose author has a false belief that
/// administration is restricted. Nothing happening is precisely what a working restriction looks like, so
/// there is no looking that finds it out.
/// </para>
/// <para>
/// <b>Why <c>access</c> is warned about rather than refused, since the argument above is
/// <see cref="UnhonouredFeatures"/>' criterion word for word.</b> A false belief the author cannot check
/// is exactly what that table refuses — the three <c>before*</c> hooks are refused because "a write the
/// author believes is vetted is neither". The difference is that a before-hook sits on a live write path,
/// so ignoring it permits something <em>now</em>; <c>access</c> governs an administration surface that does
/// not exist in this build at all, so today nothing is wrongly permitted and refusing the descriptor would
/// refuse it for being ahead of the implementation. That is also what ties this entry to #146's ordering:
/// the day the surface lands, <c>access</c> is either honoured or refused — never warned about.
/// </para>
/// <para>
/// <b><c>realtime</c> is absent for a different and sharper reason: it is not a top-level block at all.</b>
/// The schema declares it per entity (<c>$defs/entity/properties/realtime</c>) as a boolean whose
/// <b>default is <c>true</c></b> — so realtime is unhonoured for <em>every</em> entity of <em>every</em>
/// descriptor, declared or not. Neither available shape is worth emitting: warning only on an explicit
/// <c>realtime: true</c> would stay silent for the overwhelming majority of entities that are equally
/// affected, and warning on all of them would fire on every descriptor ever applied, which is the
/// unconditional line every operator learns to filter out. It is recorded in
/// <c>docs/architecture/data-api.md</c> and tracked as its own issue instead, where it can say the true
/// thing this table's shape cannot.
/// </para>
/// <para>
/// <b>Two entries are now <em>partially</em> honoured, and the wording carries that rather than the entry
/// leaving.</b> An after-hook does render a <c>templates</c> entry and does post to a <c>webhooks</c>
/// endpoint, so "nothing renders a template" and "no event is ever delivered" stopped being true — but the
/// same two blocks are still dead from <c>automation</c>, which is where most descriptors reference them.
/// Deleting the entries would have been the larger lie. What replaced them names both halves, and the
/// <c>webhooks</c> line names the absence an author is likeliest to assume away: a delivery that happens is
/// <b>unsigned</b> — <c>secretRef</c> is not read, no Standard Webhooks HMAC header is sent — and
/// unprojected. An unsigned delivery an author believes is signed is a security absence, which is exactly
/// the misattribution this table exists for.
/// </para>
/// <para>
/// Entries leave as subsystems land, exactly as <see cref="UnhonouredFeatures"/>' do: the PR that
/// implements webhooks <em>from automation</em>, signs the delivery and reads a <c>bodyFile</c> deletes these
/// two entries, and the warning stops naming them.
/// </para>
/// </remarks>
internal static partial class UnhonouredSubsystems
{
    /// <summary>
    /// Every top-level block the schema declares, this build parses, and nothing acts on — ordered as the
    /// schema declares them, so the warning's order is a property of the schema rather than of this file.
    /// </summary>
    internal static IReadOnlyList<UnhonouredSubsystem> All { get; } =
    [
        new(
            "dynamicEntities",
            descriptor => descriptor.DynamicEntities?.Enabled == true,
            "no runtime entity can be created and the whole dynamic schema-registry driver is absent, so "
            + "every governance limit declared here bounds nothing (F7)"),
        new(
            "access",
            descriptor => descriptor.Access is { } access
                && (access.Admin is not null || access.Developer is not null || access.Viewer is not null),
            "no management level is enforced anywhere, so a project an author believes only an admin may "
            + "administer is administrable by whoever the host lets in — and the CEL is not compiled either, "
            + "so a rule that could never evaluate is not reported as one (#146)"),
        new(
            "automation",
            descriptor => descriptor.Automation is { Count: > 0 },
            "no rule is ever evaluated, so no declared action runs — which looks exactly like a condition "
            + "that never matched"),
        new(
            "templates",
            descriptor => descriptor.Templates is { Count: > 0 },
            "a template referenced by an after-hook 'email' action is rendered, but one referenced only from an "
            + "automation rule is not, because no rule is evaluated yet — and a 'bodyFile' is not read on either "
            + "path"),
        new(
            "webhooks",
            descriptor => descriptor.Webhooks?.Endpoints is { Count: > 0 },
            "an endpoint an after-hook posts to is delivered to, but one referenced only from an automation rule "
            + "never receives anything; and no delivery is signed — 'secretRef' is not read and no Standard "
            + "Webhooks HMAC header is sent, so a receiver cannot yet verify the sender (7.1), nor is the "
            + "payload projected per endpoint (#152)"),
        new(
            "functions",
            descriptor => descriptor.Functions is { Count: > 0 },
            "no function is ever invoked, on any trigger or schedule it declares"),
    ];

    /// <summary>
    /// The blocks <paramref name="descriptor"/> declares that this build honours nowhere, in
    /// <see cref="All"/>'s order.
    /// </summary>
    /// <param name="descriptor">The descriptor just accepted as authoritative.</param>
    internal static IReadOnlyList<UnhonouredSubsystem> DeclaredBy(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return [.. All.Where(subsystem => subsystem.IsDeclaredBy(descriptor))];
    }

    /// <summary>
    /// Writes the one warning an applied descriptor's unhonoured blocks earn — <b>naming each of them</b> —
    /// or nothing at all when it declares none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One line for the whole set rather than one per block.</b> The set is a property of the descriptor,
    /// not of each block, and an author reading five separate warnings has to reassemble the list this
    /// already gives them. It also keeps the fact that pins this behaviour honest: a test asserting
    /// "a warning was logged" would pass on any wording, so
    /// <c>UnhonouredSubsystemsTests</c> asserts <em>which blocks the line names</em>, which only works
    /// because there is exactly one line to read.
    /// </para>
    /// <para>
    /// The block names are carried as their own structured field as well as being in the message, so a host
    /// aggregating logs can group on the set without parsing prose.
    /// </para>
    /// </remarks>
    /// <param name="logger">The logger the applying service writes through.</param>
    /// <param name="descriptor">The descriptor just accepted as authoritative.</param>
    internal static void Warn(ILogger logger, AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var declared = DeclaredBy(descriptor);
        if (declared.Count == 0)
        {
            return;
        }

        DeclaresUnhonouredBlocks(
            logger,
            declared.Count,
            string.Join(", ", declared.Select(subsystem => subsystem.Block)),
            string.Join("; ", declared.Select(subsystem => $"{subsystem.Block}: {subsystem.Consequence}")));
    }

    /// <summary>
    /// The one warning, as a compile-time-generated <c>LoggerMessage</c> delegate.
    /// </summary>
    /// <remarks>
    /// Source-generated rather than a <c>LogWarning</c> call because <c>CA1848</c> is an error in this
    /// repository — and the analyzer is right here for a reason that outlives the rule: this runs on the apply
    /// path, and boxing three arguments into a <c>params object?[]</c> on a path that also compiles every CEL
    /// rule in the project is a cost with no upside.
    /// </remarks>
    /// <param name="logger">The logger the applying service writes through.</param>
    /// <param name="unhonouredBlockCount">How many unhonoured blocks the descriptor declares.</param>
    /// <param name="unhonouredBlocks">Their names, comma-separated — the part a reader acts on.</param>
    /// <param name="unhonouredConsequences">What does not happen, per block.</param>
    /// <remarks>
    /// <b>The preamble no longer offers "because their absence is observable" as the blanket reason.</b>
    /// That was true when every entry was on limb one, and it is false for <c>access</c> — which is on the
    /// table precisely because its absence is <em>not</em> observable. It was therefore the one sentence in
    /// the one line an operator reads that pointed the reassuring way about the one security-relevant block
    /// it names. What replaced it holds for both limbs and says nothing about observability.
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "This descriptor declares {UnhonouredBlockCount} block(s) this build does not honour: "
            + "{UnhonouredBlocks}. They are accepted rather than refused, because a warning can carry what "
            + "is missing, but nothing runs for them — {UnhonouredConsequences}.")]
    private static partial void DeclaresUnhonouredBlocks(
        ILogger logger,
        int unhonouredBlockCount,
        string unhonouredBlocks,
        string unhonouredConsequences);
}

/// <summary>
/// One top-level descriptor block this build parses and honours nowhere.
/// </summary>
/// <param name="Block">
/// The block's key at the descriptor root, spelled exactly as <c>schema/project.schema.json</c> declares
/// it — which a fact asserts against the schema itself, because an entry naming a key no descriptor can
/// carry warns about nothing.
/// </param>
/// <param name="IsDeclaredBy">
/// Whether a parsed descriptor really declares it. A block present but declined by value — an empty
/// collection, or <c>enabled: false</c> — is not a declaration, so this is a predicate rather than a
/// null check.
/// </param>
/// <param name="Consequence">
/// What does not happen, concretely, and where an author would otherwise misattribute it. Never the word
/// "unsupported" alone, on <see cref="UnhonouredFeature{T}"/>'s precedent.
/// </param>
internal sealed record UnhonouredSubsystem(
    string Block,
    Func<AlvoDescriptor, bool> IsDeclaredBy,
    string Consequence);
