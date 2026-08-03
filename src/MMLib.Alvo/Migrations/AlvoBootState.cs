using System.Collections.Immutable;

namespace MMLib.Alvo.Migrations;

/// <summary>How far Alvo's boot has got.</summary>
public enum AlvoBootPhase
{
    /// <summary>
    /// The boot has not published anything yet, so nothing may be served from Alvo's schema or rules.
    /// </summary>
    /// <remarks>
    /// <b>Zero deliberately, so <c>default(AlvoBootPhase)</c> is the closed one.</b> A readiness probe that
    /// answers before the boot ran — or against a state nothing ever published to — must report "not ready";
    /// the same reasoning that puts <see cref="AlvoSchemaStartupMode.Verify"/> at zero.
    /// </remarks>
    Pending = 0,

    /// <summary>
    /// The boot decided the schema, primed the policy catalog from it, and the process may serve.
    /// </summary>
    Ready = 1,

    /// <summary>
    /// The boot refused. The process is not serving Alvo, and <see cref="AlvoBootState.Failure"/> says why.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// What the boot published about itself: whether it finished, the applied revision it primed from, and the
/// refusal if it refused. Registered as a singleton by <c>AddAlvo</c>, written once during the host's
/// <c>StartingAsync</c>, and read by a readiness probe on every request thereafter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by project, over a collection that today has exactly one entry.</b>
/// <c>IPolicyCatalogProvider.SetCurrent</c> and <see cref="IAppliedSchemaStore"/> are already
/// project-keyed, so this matches the grain the data model already has, and serving several projects from one
/// host (#141, parked) needs project-aware <em>accessors</em> rather than a different state machine. The
/// members below are the process-level view of that collection: <see cref="Phase"/> is Ready only when every
/// booted project is, and <see cref="AppliedRevision"/> is published only while there is exactly one project
/// to publish it for. Nothing here reports Ready for a collection that is still empty, which is the fail-closed
/// direction: an unprimed policy catalog denies every operation, so a probe that answered Ready first would
/// route traffic to a process that can only 403.
/// </para>
/// <para>
/// <b>A published failure is terminal for the process, and that is a decision rather than an oversight.</b>
/// <see cref="Phase"/> short-circuits on <see cref="Failure"/>, so no later <see cref="Ready"/> can restore the
/// phase and nothing clears it. Every path that records one either stops the start or freezes the route table it
/// was recording about, so "recovered, and ready again" is not a state this process can reach — and a state
/// machine that allowed it would have to define what a half-recovered boot serves, which nothing here can
/// answer. A restart builds a new singleton, which is the intended way out.
/// <c>AlvoBootStateTests.A_published_failure_is_terminal_even_if_a_project_later_reports_ready</c> pins it, so
/// the one-directionality is a documented property rather than an accident of the expression's order.
/// <b>#141 is where it has to change</b>: <see cref="Failure"/> is a single string while the collection is
/// project-keyed, so with several projects in one host one project's refusal masks every other project's
/// readiness — the right conservative answer for one project and the wrong one for several.
/// </para>
/// <para>
/// <b>Thread safety is an immutable snapshot published by an interlocked swap, not three volatile fields.</b>
/// The three members must be mutually consistent — a probe that read <see cref="Phase"/> as Ready and then
/// <see cref="AppliedRevision"/> as absent would report a state that never existed — and only a single-reference
/// publication gives that. Each write builds a whole new snapshot and installs it with
/// <see cref="ImmutableInterlocked"/>, whose <c>Interlocked.CompareExchange</c> is a full fence: the snapshot
/// and everything it holds are immutable and fully constructed before the reference becomes visible, so no
/// reader can observe a half-built one even on a weakly ordered architecture. Each read is a
/// <c>Volatile.Read</c>, whose acquire semantics stop a reader from using a stale cached reference or hoisting
/// reads of the snapshot's contents above the load of the reference itself. No lock, so a readiness probe can
/// never queue behind a boot.
/// </para>
/// </remarks>
public sealed class AlvoBootState
{
    private BootSnapshot _snapshot = BootSnapshot.NothingBootedYet;

    /// <summary>Gets the phase every booted project has reached, or <see cref="AlvoBootPhase.Pending"/> when none has.</summary>
    public AlvoBootPhase Phase => Current.Phase;

    /// <summary>
    /// Gets the applied schema revision the boot primed from, or <see langword="null"/> when it has none —
    /// nothing booted yet, the boot refused, the boot adopted a database whose live schema already matched the
    /// descriptor so there was nothing to record, or (#141) more than one project is booted and there is no one
    /// revision to report.
    /// </summary>
    /// <remarks>
    /// This is Alvo's <c>status.observedGeneration</c>: readiness is this revision matching the descriptor the
    /// process actually primed from, which is the one comparison that serves a probe, the CLI and a dashboard
    /// identically.
    /// </remarks>
    public int? AppliedRevision => Current.AppliedRevision;

    /// <summary>
    /// Gets the operator-readable reason the boot refused, or <see langword="null"/> while it has not.
    /// </summary>
    /// <remarks>
    /// <b>For a log or an authenticated diagnostic, never for an anonymous response body.</b> A refusal Alvo
    /// composed itself names only schema steps and configuration keys, but a stage-1 or stage-2 failure carries
    /// the <em>provider's</em> message, which can hold a connection string or a file path. A readiness probe is
    /// by design unauthenticated, so whatever answers one must report the phase and not this text.
    /// </remarks>
    public string? Failure => Current.Failure;

    /// <summary>Publishes a project as booted, primed and servable.</summary>
    /// <param name="project">The project that booted.</param>
    /// <param name="appliedRevision">The applied revision it primed from, or <see langword="null"/> when it read none.</param>
    internal void Ready(string project, int? appliedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        Publish(snapshot => snapshot.With(project, new ProjectBootState(AlvoBootPhase.Ready, appliedRevision)));
    }

    /// <summary>Publishes a refusal that happened before the boot knew which project it was booting.</summary>
    /// <param name="reason">The operator-readable reason.</param>
    /// <remarks>
    /// Stage 0 can fail while loading or validating the descriptor, i.e. before a project name exists. Such a
    /// failure must still leave the phase <see cref="AlvoBootPhase.Failed"/> rather than
    /// <see cref="AlvoBootPhase.Pending"/>: the two are equally closed, but only one of them carries the reason
    /// an operator has to read.
    /// </remarks>
    internal void Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Publish(snapshot => snapshot with { Failure = reason });
    }

    /// <summary>Publishes a refusal for one project.</summary>
    /// <param name="project">The project whose boot refused.</param>
    /// <param name="reason">The operator-readable reason.</param>
    internal void Failed(string project, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Publish(snapshot => snapshot
            .With(project, new ProjectBootState(AlvoBootPhase.Failed, AppliedRevision: null))
            .Refusing(reason));
    }

    private BootSnapshot Current => Volatile.Read(ref _snapshot);

    private void Publish(Func<BootSnapshot, BootSnapshot> transition)
        => ImmutableInterlocked.Update(ref _snapshot, transition);

    /// <summary>Everything the state reports, as one value published atomically.</summary>
    /// <param name="Projects">What each project that has published something reached.</param>
    /// <param name="Failure">The reason the boot refused, or <see langword="null"/>.</param>
    private sealed record BootSnapshot(ImmutableDictionary<string, ProjectBootState> Projects, string? Failure)
    {
        internal static BootSnapshot NothingBootedYet { get; } =
            new(ImmutableDictionary.Create<string, ProjectBootState>(StringComparer.Ordinal), Failure: null);

        internal AlvoBootPhase Phase => this switch
        {
            { Failure: not null } => AlvoBootPhase.Failed,
            { Projects.Count: 0 } => AlvoBootPhase.Pending,
            _ => EveryProjectIsReady ? AlvoBootPhase.Ready : AlvoBootPhase.Pending,
        };

        internal int? AppliedRevision =>
            Projects.Count == 1 ? Projects.Values.First().AppliedRevision : null;

        internal BootSnapshot With(string project, ProjectBootState state) =>
            this with { Projects = Projects.SetItem(project, state) };

        internal BootSnapshot Refusing(string reason) => this with { Failure = reason };

        private bool EveryProjectIsReady =>
            Projects.Values.All(project => project.Phase is AlvoBootPhase.Ready);
    }

    /// <summary>What one project's boot reached.</summary>
    /// <param name="Phase">The phase it reached.</param>
    /// <param name="AppliedRevision">The applied revision it primed from, if any.</param>
    private sealed record ProjectBootState(AlvoBootPhase Phase, int? AppliedRevision);
}
