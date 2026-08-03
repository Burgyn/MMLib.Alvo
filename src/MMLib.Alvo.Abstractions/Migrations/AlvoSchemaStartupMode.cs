namespace MMLib.Alvo.Migrations;

/// <summary>
/// What a boot is allowed to do when the project descriptor no longer matches the schema already applied
/// to the database — the one setting that decides whether starting a process can run DDL.
/// </summary>
/// <remarks>
/// <para>
/// The mode governs <b>drift only</b>. An <em>uninitialized</em> database — no applied snapshot at all — is
/// initialized in every mode except <see cref="Skip"/>, because the hazard the mode exists to guard against
/// is migrating a database that already holds data, not creating an empty one. An <em>unchanged</em>
/// descriptor serves in every mode and runs no DDL.
/// </para>
/// <para>
/// <b><see cref="Verify"/> is the zero value and <see cref="Apply"/> is the configured default, and the two
/// differ on purpose.</b> Zero is what a <em>lost</em> value lands on — an uninitialized field, a
/// <c>default(AlvoSchemaStartupMode)</c>, a struct nobody configured — and losing a value must never be how a
/// process earns the right to rewrite a schema. What a host actually gets when it says nothing is
/// <c>AlvoSchemaOptions.Startup</c>'s default, which is <see cref="Apply"/>, because saying nothing is a
/// choice a developer made about their own database rather than a value that went missing.
/// </para>
/// <para>
/// A value outside this enumeration is refused at startup, because it cannot be refused anywhere else:
/// the configuration binder accepts a bare number (<c>"42"</c> binds to <c>(AlvoSchemaStartupMode)42</c>
/// with no error), and a <c>switch</c> over an undefined mode would silently take some arm nobody chose.
/// </para>
/// </remarks>
public enum AlvoSchemaStartupMode
{
    /// <summary>
    /// Refuse to start on drift, printing the steps that differ and how to apply them. Reads the applied
    /// snapshot and runs no DDL.
    /// </summary>
    /// <remarks>
    /// <b>What a production deployment should set</b>, together with a migration job that applies the
    /// descriptor once — see <c>docs/architecture/host.md</c>. It is not the default: a replica set on a
    /// rolling deploy would otherwise have every replica attempt DDL with rights the application should not
    /// need in production, which is the one cost of the default being <see cref="Apply"/>. Reaching for this
    /// is therefore an opt-out, not an opt-in.
    /// </remarks>
    Verify = 0,

    /// <summary>
    /// Apply the drift on boot. A <em>destructive</em> plan is still refused unless
    /// <c>AlvoSchemaOptions.AllowDestructive</c> is set — this mode buys "apply on boot", never
    /// "lose data on boot".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The default, because it is the loop the product exists for: edit the descriptor, restart, it
    /// works.</b> Exempting <em>initialization</em> from the mode saves only the first run; the second run,
    /// after the first edit, is drift — which is exactly when somebody is working. And the protection was
    /// never in the mode: the destructive gate is separate and always on, so no mode can drop a column or a
    /// table without <c>AlvoSchemaOptions.AllowDestructive</c>, including while initializing. What this mode
    /// actually permits is <em>additive</em> DDL, and a host that had the rights for it on run 1 has them on
    /// run 2.
    /// </para>
    /// <para>
    /// The cost is real and is not hidden: every replica of a rolling deployment attempts the DDL (they
    /// converge rather than crash-looping, but they all try), and the application needs DDL rights in
    /// production, which is what EF Core's own guidance advises against. Production wants
    /// <see cref="Verify"/> plus a migration job.
    /// </para>
    /// </remarks>
    Apply = 1,

    /// <summary>
    /// Never touch the project schema: do not read the applied snapshot, do not initialize, do not apply.
    /// For a host whose schema is owned entirely by a separate migration job.
    /// </summary>
    Skip = 2,
}
