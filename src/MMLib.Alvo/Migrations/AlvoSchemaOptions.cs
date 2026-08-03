namespace MMLib.Alvo.Migrations;

/// <summary>
/// How much the boot sequence is allowed to do to the project schema. Bound from the
/// <see cref="SectionName"/> configuration section by <c>AddAlvo</c> and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>A host that says nothing gets <see cref="AlvoSchemaStartupMode.Apply"/> and no destructive
/// allowance</b> — the descriptor is applied on boot, and no boot in any mode may discard data without
/// <see cref="AllowDestructive"/>. The two halves are deliberately separate: the mode decides whether a
/// process may bring the database up to the descriptor, and the allowance decides whether it may throw
/// anything away doing so. Only the first is defaulted permissively.
/// </para>
/// <para>
/// <b>Applying by default is the loop the product exists for</b> — edit the descriptor, restart, it works.
/// The alternative default, <see cref="AlvoSchemaStartupMode.Verify"/>, breaks it on the <em>second</em> run:
/// the first run is an initialization, which no mode governs, but the run after the first edit is drift, and
/// that is precisely when somebody is working. Its cost is stated where it is paid — a production replica set
/// wants <see cref="AlvoSchemaStartupMode.Verify"/> and a migration job
/// (<c>docs/architecture/host.md</c>) — so production is an opt-out rather than the dev loop being an opt-in.
/// </para>
/// <para>
/// In an environment variable the keys are spelled with a double underscore for the separator —
/// <c>Alvo__Schema__Startup</c> and <c>Alvo__Schema__AllowDestructive</c>.
/// </para>
/// </remarks>
public sealed class AlvoSchemaOptions
{
    /// <summary>The configuration section these options bind from: <c>Alvo:Schema</c>.</summary>
    public const string SectionName = "Alvo:Schema";

    /// <summary>
    /// The environment-variable spelling of <see cref="Startup"/>, quoted verbatim by every refusal that
    /// tells an operator how to change the mode.
    /// </summary>
    /// <remarks>
    /// A constant rather than a literal per message: the bad-mode refusal and the drift refusal are built in
    /// different types, and an operator who is told two different key names for one setting learns to trust
    /// neither.
    /// </remarks>
    internal const string StartupEnvironmentVariable = "Alvo__Schema__Startup";

    /// <summary>
    /// The environment-variable spelling of <see cref="AllowDestructive"/>, quoted verbatim by the refusal
    /// that reports a plan which would discard data.
    /// </summary>
    internal const string AllowDestructiveEnvironmentVariable = "Alvo__Schema__AllowDestructive";

    /// <summary>
    /// Gets or sets what a boot does when the descriptor has drifted from the applied schema. Defaults to
    /// <see cref="AlvoSchemaStartupMode.Apply"/>, which brings the database up to the descriptor and still
    /// refuses any step that would discard data.
    /// </summary>
    /// <remarks>
    /// <b>This default is deliberately <em>not</em> <c>default(AlvoSchemaStartupMode)</c>, which is
    /// <see cref="AlvoSchemaStartupMode.Verify"/>.</b> The two answer different questions: the zero value is
    /// where a value that went <em>missing</em> lands, and it must be the mode that touches nothing; this
    /// initializer is where a host that deliberately said nothing lands, and that host pointed Alvo at its own
    /// database on purpose. Anything that collapses the two — reading the property's default off the enum, or
    /// moving <see cref="AlvoSchemaStartupMode.Apply"/> to zero to "match" — loses one of the two guarantees.
    /// </remarks>
    public AlvoSchemaStartupMode Startup { get; set; } = AlvoSchemaStartupMode.Apply;

    /// <summary>
    /// Gets or sets whether a boot may apply a plan that drops or narrows something — the guardrail that
    /// separates <see cref="AlvoSchemaStartupMode.Apply"/> from data loss. Defaults to <c>false</c>, so a
    /// destructive plan is refused even under <see cref="AlvoSchemaStartupMode.Apply"/>.
    /// </summary>
    public bool AllowDestructive { get; set; }
}
