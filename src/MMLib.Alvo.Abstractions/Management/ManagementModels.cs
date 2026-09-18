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
