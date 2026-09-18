namespace MMLib.Alvo.Migrations;

/// <summary>
/// What an apply <em>would</em> do — the migration plan, the base it was planned against, and the
/// destructive guardrail's verdict. Nothing is written to produce one.
/// </summary>
/// <remarks>
/// <b>The verdict is reported, not thrown.</b> <see cref="RuntimeSchemaService.ApplyAsync"/> throws
/// <see cref="DestructiveChangeNotAllowedException"/> because it must not proceed; a preview exists precisely
/// to show the caller the plan <em>and</em> the verdict, and an exception would hand them the second without
/// the first. The schema editor's diff, the rollback preview and the later AI proposal card are one mechanism
/// with three consumers (the F5 design §2.1).
/// </remarks>
/// <param name="Plan">The migration this descriptor would produce against the current applied schema.</param>
/// <param name="CurrentRevision">The revision the plan was built against; 0 when nothing is applied yet.</param>
/// <param name="AllowedByGuardrail">
/// Whether <see cref="RuntimeSchemaService.ApplyAsync"/> would proceed: <see langword="false"/> exactly when
/// the plan is destructive and <see cref="MigrationOptions.AllowDestructive"/> is not set.
/// </param>
public sealed record DescriptorApplyPreview(MigrationPlan Plan, int CurrentRevision, bool AllowedByGuardrail);
