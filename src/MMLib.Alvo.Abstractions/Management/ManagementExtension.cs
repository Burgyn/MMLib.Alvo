using System.Text.Json;

namespace MMLib.Alvo.Management;

/// <summary>
/// One piece of host code registered with this instance, as metadata: what it is, what it binds to and when it runs.
/// Never the code itself, and never a way to change it.
/// </summary>
/// <remarks>
/// Init-only rather than positional, so a later version adds members without breaking a reader or a constructor call.
/// </remarks>
public sealed record ManagementExtension
{
    /// <summary>Gets the stable id the extension was registered under; its durable state is keyed by it.</summary>
    public required string Id { get; init; }

    /// <summary>Gets what kind of host code it is.</summary>
    public required AlvoExtensionKind Kind { get; init; }

    /// <summary>Gets when, relative to a write, it runs.</summary>
    public required AlvoExtensionStage Stage { get; init; }

    /// <summary>
    /// Gets the entities it binds: names from the descriptor, <c>*</c> alone for every entity, empty for an extension
    /// bound to an application event rather than to an entity.
    /// </summary>
    public IReadOnlyList<string> Entities { get; init; } = [];

    /// <summary>
    /// Gets the data operations it runs on, as the policy names them: <c>create</c>, <c>update</c> (a replace included),
    /// <c>delete</c>; empty for an extension bound to an application event.
    /// </summary>
    public IReadOnlyList<string> Operations { get; init; } = [];

    /// <summary>Gets the application or framework event type it handles, when it handles one rather than an entity's writes.</summary>
    public string? Event { get; init; }

    /// <summary>Gets its condition as CEL text, or <see langword="null"/> when it runs on every event it selects.</summary>
    public string? When { get; init; }

    /// <summary>Gets where it sits among the extensions of the same target; lower first, then registration order.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Gets how long one attempt may take before it is cancelled and counted as failed; <see langword="null"/> for a
    /// kind that has no timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets who it acts as: <c>system</c> (the system in the event's tenant) or <c>eventActor</c>;
    /// <see langword="null"/> for a kind that has no identity of its own.
    /// </summary>
    public string? RunAs { get; init; }

    /// <summary>
    /// Gets whether one record's events reach it in order: <c>perRecord</c> or <c>none</c>; <see langword="null"/> for a
    /// kind that receives no events.
    /// </summary>
    public string? Ordering { get; init; }

    /// <summary>Gets what it does, in the host developer's own sentence, when they wrote one.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the .NET type that implements it, or <see langword="null"/> unless the host set
    /// <c>Alvo:Extensions:ExposeTypeNames</c>; a delegate is never named.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Gets the route pattern a command was mapped at, as the host wrote it — a <c>MapGroup</c> prefix is not included;
    /// null for an extension with no route of its own.
    /// </summary>
    public string? Route { get; init; }

    /// <summary>Gets the HTTP method of <see cref="Route"/>; null when there is no route.</summary>
    public string? HttpMethod { get; init; }

    /// <summary>
    /// Gets a command's access rule as CEL text — a condition over <c>@user</c>, <c>@tenant</c> and, for a command bound to
    /// an entity, the row; null for an extension that has none.
    /// </summary>
    public string? Access { get; init; }

    /// <summary>
    /// Gets whether an operator paused it: its deliveries are still recorded, wait, and run in order once it is resumed.
    /// </summary>
    public bool Paused { get; init; }

    /// <summary>
    /// Gets whether it runs only for a write no transaction unit is open around — a validate hook, whose network call must
    /// never hold a transaction open; a write made inside a unit skips it.
    /// </summary>
    public bool TopLevelOnly { get; init; }

    /// <summary>Gets whether it runs: only a validate hook can be switched off, by an administrator.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets a schedule's five-field cron; null for any other kind.</summary>
    public string? Cron { get; init; }

    /// <summary>Gets the IANA time zone a schedule's cron is read in; null for any other kind.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Gets when a schedule next runs, in UTC; null for any other kind, or before its first start has recorded it.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Gets the occurrence a schedule last ran for, in UTC; null for any other kind, or before its first run.</summary>
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>
    /// Gets the status of a schedule's newest delivery — <c>pending</c>, <c>running</c>, <c>retrying</c>, <c>succeeded</c> or
    /// <c>dead</c>; null for any other kind, or before its first run.
    /// </summary>
    /// <remarks>
    /// A per-tenant schedule's occurrence is one delivery per tenant, and this is the status of the newest of them alone — one
    /// tenant's run; the delivery list, filtered by the schedule's id, shows every tenant's.
    /// </remarks>
    public string? LastOutcome { get; init; }

    /// <summary>Gets the descriptor revision a data migration waits for before it runs; null for any other kind.</summary>
    public int? AfterRevision { get; init; }

    /// <summary>
    /// Gets an event handler's cap on its running deliveries, as key and limit — <c>tenant:2</c>, <c>record:1</c>; null for
    /// a handler with no cap, or any other kind.
    /// </summary>
    public string? Concurrency { get; init; }

    /// <summary>
    /// Gets a function's input as a JSON Schema document, as its input type exports it; null for any other kind.
    /// </summary>
    public JsonElement? InputSchema { get; init; }
}
