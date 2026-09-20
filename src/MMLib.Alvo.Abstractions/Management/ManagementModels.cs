namespace MMLib.Alvo.Management;

/// <summary>What this Alvo instance is.</summary>
/// <param name="Version">The informational version of the running <c>MMLib.Alvo</c> assembly.</param>
/// <param name="Mode">
/// <c>standalone</c> or <c>embedded</c> — the host's own label when it set one, otherwise the registered
/// <see cref="AlvoMode"/>, lower-cased.
/// </param>
/// <param name="DataProvider">
/// The registered <c>IAlvoData</c> implementation's type name, or <c>none</c> when no driver is registered.
/// <b>Not the database engine:</b> the core may not reference the adapter that knows one.
/// </param>
/// <param name="StartupMode">The <c>Alvo:Schema:Startup</c> mode this process booted under, lower-cased.</param>
public sealed record ManagementInfo(string Version, string Mode, string DataProvider, string StartupMode);

/// <summary>One project this instance serves.</summary>
/// <param name="Name">The project name — the descriptor's own <c>name</c>.</param>
/// <param name="Revision">The applied revision, or <see langword="null"/> when nothing has been applied yet.</param>
/// <param name="Phase">The boot phase, lower-cased: <c>pending</c>, <c>ready</c> or <c>failed</c>.</param>
public sealed record ManagementProject(string Name, int? Revision, string Phase);

/// <summary>A project's current descriptor and the revision an apply must echo.</summary>
/// <param name="Project">The project name.</param>
/// <param name="Revision">The current revision; <c>0</c> when nothing has been applied yet.</param>
/// <param name="DescriptorJson">
/// The descriptor exactly as it was applied. <b>Not a re-serialisation:</b> what a caller exports is the
/// stored text, which is the only shape under which "everything clickable is exportable as code" can be
/// true without drift.
/// </param>
public sealed record ManagementDescriptor(string Project, int Revision, string DescriptorJson);

/// <summary>One entry in a project's append-only configuration history.</summary>
/// <param name="Revision">The revision number; the first applied revision is 1.</param>
/// <param name="CreatedAt">When it was appended.</param>
/// <param name="Author">Who appended it; <see langword="null"/> for code-first or system.</param>
/// <param name="Reason">The human- or agent-supplied reason, when one was given.</param>
/// <param name="RolledBackFrom">The revision this one restored, when it was produced by a rollback.</param>
public sealed record ManagementRevision(
    int Revision, DateTimeOffset CreatedAt, string? Author, string? Reason, int? RolledBackFrom);

/// <summary>One revision with the descriptor it applied.</summary>
/// <param name="Version">The revision's provenance.</param>
/// <param name="DescriptorJson">The descriptor exactly as it was applied at that revision.</param>
public sealed record ManagementRevisionDetail(ManagementRevision Version, string DescriptorJson);

/// <summary>What this build honours, warns about, and refuses — one source of truth for "not yet".</summary>
/// <param name="Honoured">The top-level blocks this build honours.</param>
/// <param name="Warned">Blocks that apply and then do nothing. The section exists; nothing runs.</param>
/// <param name="Refused">Features an apply rejects. A control for one of these must not exist.</param>
public sealed record ManagementCapabilities(
    IReadOnlyList<string> Honoured,
    IReadOnlyList<ManagementWarnedBlock> Warned,
    IReadOnlyList<ManagementRefusedFeature> Refused);

/// <summary>A declared block this build parses and then honours nowhere.</summary>
/// <param name="Block">The descriptor's top-level block name.</param>
/// <param name="Consequence">
/// What does not happen, concretely — <b>served verbatim.</b> Never rewrite it in a client: it is the
/// framework's own sentence, and a second wording is a third spelling of one truth.
/// </param>
public sealed record ManagementWarnedBlock(string Block, string Consequence);

/// <summary>A declared feature this build refuses at apply.</summary>
/// <param name="Slot">The feature's qualified name, e.g. <c>field.default</c>.</param>
/// <param name="Consequence">What would silently happen instead — <b>served verbatim.</b></param>
/// <param name="Fix">What to do instead, and where it is tracked — <b>served verbatim.</b></param>
public sealed record ManagementRefusedFeature(string Slot, string Consequence, string Fix);

/// <summary>One policy question.</summary>
/// <remarks>
/// There is deliberately no record id. Evaluating a predicate against a <em>stored row</em> needs a read,
/// and a read through the Management API is the data surface deviation D4 refuses to create — a caller who
/// wants to know whether one row passes fetches it through the Data API under the simulated caller's own
/// credential, which is the production answer by construction.
/// </remarks>
/// <param name="Entity">The entity name, matched ordinally against the descriptor's own.</param>
/// <param name="Operation">
/// The operation's wire name: <c>list</c>, <c>get</c>, <c>create</c>, <c>update</c> or <c>delete</c>.
/// </param>
/// <param name="Caller">The caller to answer for.</param>
public sealed record ManagementPolicySimulation(string Entity, string Operation, ManagementSimulatedCaller Caller);

/// <summary>A caller to answer a policy question for.</summary>
/// <param name="User">
/// The caller's id, or <see langword="null"/> to simulate the anonymous caller — who holds only <c>anon</c>,
/// so <paramref name="Roles"/> must then be empty. Production has no credential that resolves to roles
/// without an identity, and answering for one would be answering a question production cannot be asked.
/// </param>
/// <param name="Roles">The role names the caller holds; each must be declared in <c>auth.roles</c> or built in.</param>
/// <param name="Tenant">
/// The tenant the caller acts in; <see langword="null"/> denies on a tenant-scoped entity, as production
/// does.
/// </param>
public sealed record ManagementSimulatedCaller(Guid? User, IReadOnlyList<string> Roles, Guid? Tenant = null);

/// <summary>What the policy engine answered, and the predicates it resolved.</summary>
/// <param name="Allowed">
/// Whether the engine resolved a policy at all, rather than refusing outright. <b>It is not "this caller
/// will see rows".</b> A rule over <c>@user.roles</c> is a predicate the engine hands back rather than
/// evaluates, so a caller no rule admits still earns <see langword="true"/> here together with a
/// <paramref name="Using"/> none of their rows satisfies. A client that rendered this alone as "permitted"
/// would be wrong exactly where it matters.
/// </param>
/// <param name="DenyReason">The engine's own reason when it refused outright; null when it did not.</param>
/// <param name="Using">The read predicate's CEL source, when one applies.</param>
/// <param name="WithCheck">The write predicate's CEL source, when one applies.</param>
/// <param name="TenantScope">The tenant-scope predicate's CEL source, when the entity is tenant-scoped.</param>
/// <param name="HiddenFields">Fields this caller may not read.</param>
/// <param name="ReadOnlyFields">Fields this caller may read and not write.</param>
public sealed record ManagementPolicyVerdict(
    bool Allowed,
    string? DenyReason,
    string? Using,
    string? WithCheck,
    string? TenantScope,
    IReadOnlyList<string> HiddenFields,
    IReadOnlyList<string> ReadOnlyFields);

/// <summary>One apply.</summary>
/// <param name="DescriptorJson">The descriptor to apply, exactly as it should be stored.</param>
/// <param name="ExpectedRevision">
/// The revision the caller believes is current — over HTTP this is <c>If-Match</c> (the F5 design's
/// deviation D3), and it is the same integer <c>schema/project.schema.json</c> froze as the
/// optimistic-concurrency token. It is what makes a retry <em>safe</em> without any key: a replayed apply
/// names a revision that is no longer current and is refused. What it does not make a retry is
/// <em>attributable</em> — see <see cref="IdempotencyKey"/>.
/// </param>
/// <param name="AllowDestructive">
/// Whether a plan that discards data may proceed. <b>Never implied</b> — not by a dry run that reported the
/// plan, and not by a caller's management level.
/// </param>
/// <param name="DryRun">
/// Plan and report without writing anything. The destructive guardrail still applies, so a dry run cannot
/// report a plan the apply would refuse.
/// </param>
/// <param name="Author">Who is applying, carried into the appended revision.</param>
/// <param name="Reason">Why, carried into the appended revision.</param>
/// <param name="IdempotencyKey">
/// A caller-chosen key that makes a retry a <b>replay</b> rather than an answer the caller cannot attribute.
/// <para>
/// <b>It buys attribution, not safety.</b> <see cref="ExpectedRevision"/> already makes the apply
/// at-most-once; what it cannot do is tell a retried caller <em>which</em> of the two things a refusal
/// means, because "my own write landed and the response was lost" and "somebody else changed the
/// descriptor" are the same 412 and need opposite recoveries. With a key the first of them answers the
/// revision the first attempt appended.
/// </para>
/// <para>
/// <b>It is refused rather than ignored where it cannot be honoured</b>: on a <see cref="DryRun"/>, which
/// appends nothing to replay; for a caller with no identity to scope it by; and past
/// <c>AlvoIdempotency.MaxKeyBytes</c> UTF-8 bytes. Reusing one key for a different request is a conflict,
/// never a replay.
/// </para>
/// </param>
public sealed record ManagementApplyRequest(
    string DescriptorJson,
    int ExpectedRevision,
    bool AllowDestructive = false,
    bool DryRun = false,
    string? Author = null,
    string? Reason = null,
    string? IdempotencyKey = null);

/// <summary>What an apply did, or would do.</summary>
/// <param name="Applied"><see langword="false"/> for a dry run, <see langword="true"/> when a revision was appended.</param>
/// <param name="Revision">The appended revision — or, for a dry run, the base it planned against.</param>
/// <param name="Plan">The migration, so the caller can show a diff without asking again.</param>
public sealed record ManagementApplyResult(bool Applied, int Revision, ManagementPlanSummary Plan);

/// <summary>A migration plan, in the shape a diff view needs.</summary>
/// <param name="IsEmpty">
/// <see langword="true"/> when the descriptor changes nothing about the schema — which a rules-only edit
/// does, and which is therefore not the same claim as "nothing was applied".
/// </param>
/// <param name="HasDestructiveChanges"><see langword="true"/> when at least one step discards data.</param>
/// <param name="Steps">
/// One line per step, destructive steps marked — <b>the framework's own summary wording</b>, the same
/// sentences a refused boot prints. A client that reworded one would be a second spelling of one truth.
/// </param>
public sealed record ManagementPlanSummary(
    bool IsEmpty, bool HasDestructiveChanges, IReadOnlyList<string> Steps);
