using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class RuntimeSchemaServiceTests
{
    private const string TasksV1 = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true }
              }
            }
          }
        }
        """;

    // Adds an optional field relative to TasksV1 — an AddField step, always non-destructive —
    // so a plan against it is non-empty without tripping the destructive guardrail.
    private const string TasksV2 = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "notes": { "type": "string" }
              }
            }
          }
        }
        """;

    // Adds a *required* field relative to TasksV1. Adding it is still non-destructive (AddField),
    // but rolling back FROM this TO TasksV1 drops that field, which IS destructive.
    private const string TasksV1WithExtra = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "assignee": { "type": "string", "required": true }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task Apply_appends_a_new_revision()
    {
        var service = CreateService();

        var v1 = await service.ApplyAsync("demo", TasksV1, expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        v1.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_with_stale_revision_conflicts()
    {
        var service = CreateService();
        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DescriptorConcurrencyException>(
            () => service.ApplyAsync("demo", TasksV2, expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apply_rejects_invalid_descriptor()
    {
        var service = CreateService();

        await Should.ThrowAsync<DescriptorValidationException>(
            () => service.ApplyAsync("demo", "{ \"apiVersion\": \"alvo.dev/v1\" }", 0, new MigrationOptions(), TestContext.Current.CancellationToken));
    }

    // Isolates RuntimeSchemaService's own staleness fail-fast (before planning) from the writer's
    // optimistic-lock re-check (after planning): Apply_with_stale_revision_conflicts above throws
    // DescriptorConcurrencyException even if the fail-fast block were deleted, because the writer's
    // own AppendAsync would independently reject the same stale expectedRevision. Only counting
    // PlanAsync invocations tells the fail-fast apart from that downstream guard.
    [Fact]
    public async Task Apply_with_stale_revision_never_calls_PlanAsync()
    {
        var store = new InMemoryDescriptorVersionStore();
        var writer = new InMemoryRuntimeSchemaWriter(store);
        var migrator = new RecordingSchemaMigrator(new InMemorySchemaMigrator());
        var validator = new DescriptorValidator();
        var service = new RuntimeSchemaService(validator, migrator, store, writer);

        await service.ApplyAsync("demo", TasksV1, expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken); // -> rev 1
        var callsAfterFirstApply = migrator.PlanAsyncCallCount;
        callsAfterFirstApply.ShouldBeGreaterThan(0);

        // expectedRevision 0 is stale now (current is 1). AllowDestructive: true so that, absent the
        // fail-fast, the flow would proceed to plan + apply rather than being stopped by the
        // destructive guardrail — the fail-fast is the ONLY thing that can prevent planning here.
        await Should.ThrowAsync<DescriptorConcurrencyException>(
            () => service.ApplyAsync(
                "demo", TasksV2, expectedRevision: 0, new MigrationOptions { AllowDestructive = true }, TestContext.Current.CancellationToken));

        migrator.PlanAsyncCallCount.ShouldBe(callsAfterFirstApply);
        (await store.ListAsync("demo", TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    // Test-only recording wrapper: delegates to an inner ISchemaMigrator (so PlanAsync's real
    // diff/plan behavior is preserved) while counting invocations, so a test can assert planning
    // never happened rather than merely asserting the eventual exception type.
    private sealed class RecordingSchemaMigrator(ISchemaMigrator inner) : ISchemaMigrator
    {
        public int PlanAsyncCallCount { get; private set; }

        public Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default)
        {
            PlanAsyncCallCount++;
            return inner.PlanAsync(current, desired, options, ct);
        }

        public Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default) =>
            inner.ApplyAsync(plan, options, ct);
    }

    [Fact]
    public async Task Apply_with_destructive_plan_without_AllowDestructive_is_refused()
    {
        var service = CreateService();
        await service.ApplyAsync("demo", TasksV1WithExtra, 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<DestructiveChangeNotAllowedException>(
            () => service.ApplyAsync("demo", TasksV1, expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken));

        ex.Project.ShouldBe("demo");
        ex.Plan.HasDestructiveChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task Rollback_appends_a_revert_revision_marked_with_source()
    {
        var service = CreateService();
        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), TestContext.Current.CancellationToken);          // rev 1
        await service.ApplyAsync("demo", TasksV1WithExtra, 1, new MigrationOptions(), TestContext.Current.CancellationToken); // rev 2

        var reverted = await service.RollbackAsync(
            "demo", targetRevision: 1, new MigrationOptions { AllowDestructive = true }, TestContext.Current.CancellationToken);

        reverted.Revision.ShouldBe(3);
        reverted.RolledBackFrom.ShouldBe(1);
        reverted.DescriptorJson.ShouldBe(TasksV1);
    }

    [Fact]
    public async Task Rollback_without_AllowDestructive_is_refused_when_destructive()
    {
        var service = CreateService();
        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), TestContext.Current.CancellationToken);
        await service.ApplyAsync("demo", TasksV1WithExtra, 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DestructiveChangeNotAllowedException>(
            () => service.RollbackAsync("demo", targetRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rollback_to_missing_revision_throws()
    {
        var service = CreateService();
        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.RollbackAsync("demo", targetRevision: 42, new MigrationOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rollback_with_no_applied_schema_throws()
    {
        var service = CreateService();

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.RollbackAsync("demo", targetRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apply_carries_author_and_reason_into_the_appended_version()
    {
        var service = CreateService();

        var v1 = await service.ApplyAsync(
            "demo", TasksV1, expectedRevision: 0,
            new MigrationOptions { Author = "agent-1", Reason = "initial schema" },
            TestContext.Current.CancellationToken);

        v1.Author.ShouldBe("agent-1");
        v1.Reason.ShouldBe("initial schema");
    }

    [Fact]
    public async Task Apply_of_a_fresh_project_with_an_empty_plan_still_appends_revision_1()
    {
        // "entities" requires at least one key, but a dynamic-storage entity is filtered out of the
        // mapped SchemaModel (DescriptorToSchemaMapper only maps physical entities) — so this still
        // maps to an empty schema, and a diff against the empty current schema is a true no-op plan.
        var emptyDescriptor = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": { "storage": "dynamic", "fields": { "text": { "type": "string" } } } } }
        """;
        var store = new InMemoryDescriptorVersionStore();
        var service = CreateService(store);

        var v1 = await service.ApplyAsync("demo", emptyDescriptor, expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        v1.Revision.ShouldBe(1);
        (await store.ListAsync("demo", TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_preserves_an_explicit_reason_instead_of_the_default_message()
    {
        var service = CreateService();
        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), TestContext.Current.CancellationToken);
        await service.ApplyAsync("demo", TasksV1WithExtra, 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        var reverted = await service.RollbackAsync(
            "demo", targetRevision: 1,
            new MigrationOptions { AllowDestructive = true, Reason = "reverting a bad deploy" },
            TestContext.Current.CancellationToken);

        reverted.Reason.ShouldBe("reverting a bad deploy");
    }

    [Fact]
    public async Task Apply_with_DryRun_is_rejected_and_appends_nothing()
    {
        var store = new InMemoryDescriptorVersionStore();
        var service = CreateService(store);

        await Should.ThrowAsync<NotSupportedException>(
            () => service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions { DryRun = true }, TestContext.Current.CancellationToken));

        (await store.ListAsync("demo", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Rollback_with_DryRun_is_rejected_and_appends_nothing()
    {
        var store = new InMemoryDescriptorVersionStore();
        var service = CreateService(store);
        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), TestContext.Current.CancellationToken);
        await service.ApplyAsync("demo", TasksV2, 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NotSupportedException>(
            () => service.RollbackAsync("demo", targetRevision: 1, new MigrationOptions { DryRun = true }, TestContext.Current.CancellationToken));

        (await store.ListAsync("demo", TestContext.Current.CancellationToken)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Apply_of_unchanged_descriptor_is_a_no_op()
    {
        var store = new InMemoryDescriptorVersionStore();
        var service = CreateService(store);
        var first = await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        var second = await service.ApplyAsync("demo", TasksV1, expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        second.ShouldBe(first);
        second.Revision.ShouldBe(1);
        (await store.ListAsync("demo", TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    // IMPORTANT: the same InMemoryDescriptorVersionStore instance is passed both to the writer
    // fake (which delegates its append there) and to the service (as its version-history read
    // port) — otherwise the writer's appends would be invisible to the service's own reads.
    private static RuntimeSchemaService CreateService() => CreateService(new InMemoryDescriptorVersionStore());

    private static RuntimeSchemaService CreateService(InMemoryDescriptorVersionStore store)
    {
        var writer = new InMemoryRuntimeSchemaWriter(store);
        var migrator = new InMemorySchemaMigrator();
        var validator = new DescriptorValidator();
        return new RuntimeSchemaService(validator, migrator, store, writer);
    }
}
