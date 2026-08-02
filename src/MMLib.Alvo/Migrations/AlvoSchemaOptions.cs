namespace MMLib.Alvo.Migrations;

/// <summary>
/// How much the boot sequence is allowed to do to the project schema. Bound from the
/// <see cref="SectionName"/> configuration section by <c>AddAlvo</c> and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// Both defaults are the ones that touch nothing: <see cref="AlvoSchemaStartupMode.Verify"/> and no
/// destructive allowance. A host that wants a boot to apply its descriptor says so, in configuration or in
/// code; a host that says nothing gets a process that reads the database and refuses to change it.
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
    /// Gets or sets what a boot does when the descriptor has drifted from the applied schema. Defaults to
    /// <see cref="AlvoSchemaStartupMode.Verify"/>, which refuses to start and runs no DDL.
    /// </summary>
    public AlvoSchemaStartupMode Startup { get; set; } = AlvoSchemaStartupMode.Verify;

    /// <summary>
    /// Gets or sets whether a boot may apply a plan that drops or narrows something — the guardrail that
    /// separates <see cref="AlvoSchemaStartupMode.Apply"/> from data loss. Defaults to <c>false</c>, so a
    /// destructive plan is refused even under <see cref="AlvoSchemaStartupMode.Apply"/>.
    /// </summary>
    public bool AllowDestructive { get; set; }
}
