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
/// <see cref="Verify"/> is deliberately the zero value, so every path that can lose the configured value —
/// an absent setting, a default-constructed options object, an uninitialized field — lands on the mode that
/// touches nothing rather than on the mode that rewrites a schema.
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
    /// snapshot and runs no DDL. The default, and the mode for production and for an Alvo embedded inside
    /// somebody else's application, where performing DDL on their database uninvited is the worse failure.
    /// </summary>
    Verify = 0,

    /// <summary>
    /// Apply the drift on boot. A <em>destructive</em> plan is still refused unless
    /// <c>AlvoSchemaOptions.AllowDestructive</c> is set — this mode buys "apply on boot", never
    /// "lose data on boot". Intended for the dev loop and for the standalone image, which sets it in its
    /// own <c>appsettings.json</c> so the policy is visible rather than compiled in.
    /// </summary>
    Apply = 1,

    /// <summary>
    /// Never touch the project schema: do not read the applied snapshot, do not initialize, do not apply.
    /// For a host whose schema is owned entirely by a separate migration job.
    /// </summary>
    Skip = 2,
}
