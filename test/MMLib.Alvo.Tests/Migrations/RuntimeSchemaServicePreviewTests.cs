using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// <see cref="RuntimeSchemaService.PreviewAsync"/> — the plan-only operation the dry-run refusal names.
/// </summary>
/// <remarks>
/// Every fact here asserts something about what a preview <em>did not</em> do, beside what it answered:
/// a preview that wrote, or that primed the policy catalog, would be an apply with a friendlier name, and
/// neither claim can be made by reading the returned plan.
/// </remarks>
public sealed class RuntimeSchemaServicePreviewTests
{
    [Fact]
    public async Task A_preview_reports_the_plan_and_writes_nothing()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(
            RuntimeSchemaWorld.TwoFields, TestContext.Current.CancellationToken);

        var preview = await world.Service.PreviewAsync(
            RuntimeSchemaWorld.Project, RuntimeSchemaWorld.ThreeFields, expectedRevision: 1,
            new MigrationOptions(), TestContext.Current.CancellationToken);

        preview.Plan.IsEmpty.ShouldBeFalse("a third field is a real step");
        preview.Plan.HasDestructiveChanges.ShouldBeFalse();
        preview.AllowedByGuardrail.ShouldBeTrue();
        preview.CurrentRevision.ShouldBe(1);
        world.Writer.Applies.ShouldBe(0, "a dry run that wrote would be an apply with a friendlier name");
        (await world.Versions.GetCurrentAsync(
            RuntimeSchemaWorld.Project, TestContext.Current.CancellationToken))!.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task A_destructive_plan_is_reported_rather_than_thrown()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(
            RuntimeSchemaWorld.TwoFields, TestContext.Current.CancellationToken);

        var preview = await world.Service.PreviewAsync(
            RuntimeSchemaWorld.Project, RuntimeSchemaWorld.OneField, expectedRevision: 1,
            new MigrationOptions(), TestContext.Current.CancellationToken);

        preview.Plan.HasDestructiveChanges.ShouldBeTrue();
        preview.AllowedByGuardrail.ShouldBeFalse(
            "a preview exists to show the verdict; throwing would leave the caller without the plan");
    }

    [Fact]
    public async Task The_same_destructive_plan_is_allowed_when_the_caller_asked_for_it()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(
            RuntimeSchemaWorld.TwoFields, TestContext.Current.CancellationToken);

        var preview = await world.Service.PreviewAsync(
            RuntimeSchemaWorld.Project, RuntimeSchemaWorld.OneField, expectedRevision: 1,
            new MigrationOptions { AllowDestructive = true }, TestContext.Current.CancellationToken);

        preview.Plan.HasDestructiveChanges.ShouldBeTrue("or the allowance below is measured against nothing");
        preview.AllowedByGuardrail.ShouldBeTrue();
    }

    [Fact]
    public async Task A_preview_against_a_base_the_caller_never_saw_is_refused()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(
            RuntimeSchemaWorld.TwoFields, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DescriptorConcurrencyException>(() => world.Service.PreviewAsync(
            RuntimeSchemaWorld.Project, RuntimeSchemaWorld.ThreeFields, expectedRevision: 0,
            new MigrationOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_invalid_descriptor_is_refused_before_anything_is_planned()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(
            RuntimeSchemaWorld.TwoFields, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.PreviewAsync(
            RuntimeSchemaWorld.Project, "{ \"apiVersion\": \"alvo.dev/v1\" }", expectedRevision: 1,
            new MigrationOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_preview_never_primes_the_policy_catalog()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(
            RuntimeSchemaWorld.TwoFields, TestContext.Current.CancellationToken);

        await world.Service.PreviewAsync(
            RuntimeSchemaWorld.Project, RuntimeSchemaWorld.ThreeFields, expectedRevision: 1,
            new MigrationOptions(), TestContext.Current.CancellationToken);

        world.PolicyCatalogs.SetCurrentCalls.ShouldBe(
            0, "a dry run that changed what the next request may do is not a dry run");
    }

    /// <summary>
    /// A preview of a fresh project plans against the empty schema and reports revision 0, rather than
    /// refusing for want of a base.
    /// </summary>
    [Fact]
    public async Task A_preview_of_a_project_with_no_history_plans_against_nothing()
    {
        var world = RuntimeSchemaWorld.Empty();

        var preview = await world.Service.PreviewAsync(
            RuntimeSchemaWorld.Project, RuntimeSchemaWorld.OneField, expectedRevision: 0,
            new MigrationOptions(), TestContext.Current.CancellationToken);

        preview.CurrentRevision.ShouldBe(0);
        preview.Plan.IsEmpty.ShouldBeFalse("the first apply creates an entity, which is a step");
        preview.AllowedByGuardrail.ShouldBeTrue();
        world.Writer.Applies.ShouldBe(0);
    }
}
