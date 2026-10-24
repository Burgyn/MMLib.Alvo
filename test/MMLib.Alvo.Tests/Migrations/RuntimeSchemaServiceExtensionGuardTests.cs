using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;
using MMLib.Alvo.Tests.Extensions;

using static MMLib.Alvo.Tests.Migrations.RuntimeSchemaWorld;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// The guard on the runtime apply path (spec 2026-10-14 §4.2, HX-03 settled details (6)): an apply, a dry run and a
/// rollback that would unbind a bound handler are refused before anything is read, planned or written; a handler that
/// was never bound blocks nothing and is reported as a warning.
/// </summary>
public sealed class RuntimeSchemaServiceExtensionGuardTests
{
    private const string NotesPointer = "/entities/tasks/fields/notes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<IPolicyCatalogProvider, ExtensionApplyGuard> OnNotes => catalogs =>
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tasks", tasks => tasks.OnUpdated("on-notes", HandlerCatalog.OnUpdated, when: "changed(notes)")));
        var binder = new ExtensionBinder(registry, new CelCompiler(), HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);
        return new ExtensionApplyGuard(registry, binder, new CelCompiler(), catalogs);
    };

    [Fact]
    public async Task An_apply_that_removes_a_field_a_bound_handler_reads_is_refused_and_writes_and_primes_nothing()
    {
        var world = await WithNotesAsync();

        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.ApplyAsync(
            Project, OneField, expectedRevision: 1, new MigrationOptions { AllowDestructive = true }, Ct));

        Pointed(refused).ShouldBe(NotesPointer);
        world.Writer.Applies.ShouldBe(0);
        world.PolicyCatalogs.SetCurrentCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_dry_run_of_it_is_refused_the_same_way()
    {
        var world = await WithNotesAsync();

        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.PreviewAsync(
            Project, OneField, expectedRevision: 1, new MigrationOptions(), Ct));

        Pointed(refused).ShouldBe(NotesPointer);
    }

    [Fact]
    public async Task The_binding_refusal_is_answered_before_the_revision_check()
    {
        var world = await WithNotesAsync();

        await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.ApplyAsync(
            Project, OneField, expectedRevision: 7, new MigrationOptions(), Ct));
    }

    [Fact]
    public async Task An_apply_that_keeps_what_the_handler_reads_is_applied()
    {
        var world = await WithNotesAsync();

        (await world.Service.ApplyAsync(Project, ThreeFields, expectedRevision: 1, new MigrationOptions(), Ct)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_rollback_to_a_revision_without_the_field_is_refused()
    {
        var versions = new InMemoryDescriptorVersionStore();
        var unguarded = Around(versions);
        await unguarded.Service.ApplyAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);
        await unguarded.Service.ApplyAsync(Project, TwoFields, expectedRevision: 1, new MigrationOptions(), Ct);
        var guarded = Around(versions, OnNotes);
        guarded.PolicyCatalogs.SetCurrent(Project, CatalogOf(TwoFields));

        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => guarded.Service.RollbackAsync(
            Project, targetRevision: 1, new MigrationOptions { AllowDestructive = true }, Ct));

        Pointed(refused).ShouldBe(NotesPointer);
        (await versions.GetCurrentAsync(Project, Ct))!.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_first_apply_a_handler_does_not_fit_is_applied_with_a_warning()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);

        var preview = await world.Service.PreviewAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);
        var applied = await world.Service.ApplyAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);

        preview.Warnings.ShouldContain(warning =>
            warning.Severity == DescriptorValidationSeverity.Warning && warning.Message.Contains("'on-notes'"));
        applied.Revision.ShouldBe(1, "a handler nothing ever bound blocks no apply; its events are recorded dead 'unbound'");
    }

    [Fact]
    public async Task A_handler_already_unbound_does_not_block_an_unrelated_apply()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);
        await world.Service.ApplyAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);

        (await world.Service.ApplyAsync(Project, OneFieldPlusRequired, expectedRevision: 1, new MigrationOptions(), Ct)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_first_apply_every_handler_fits_is_applied_without_a_warning()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);

        var preview = await world.Service.PreviewAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct);

        preview.Warnings.ShouldNotContain(warning => warning.Message.Contains("'on-notes'"));
        (await world.Service.ApplyAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct)).Revision.ShouldBe(1);
    }

    [Fact]
    public async Task An_apply_and_a_reapply_publish_the_very_catalog_the_guard_checked()
    {
        var (guard, binder, compiles) = Watched();
        var world = Around(new InMemoryDescriptorVersionStore(), guard);

        await world.Service.ApplyAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct);
        ShouldBeTheCheckedCatalog(world, binder, compiles);
        await world.Service.ApplyAsync(Project, TwoFields, expectedRevision: 1, new MigrationOptions(), Ct);
        ShouldBeTheCheckedCatalog(world, binder, compiles);
    }

    [Fact]
    public async Task A_rollback_publishes_the_very_catalog_the_guard_checked()
    {
        var (guard, binder, compiles) = Watched();
        var world = Around(new InMemoryDescriptorVersionStore(), guard);
        await world.Service.ApplyAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct);
        await world.Service.ApplyAsync(Project, ThreeFields, expectedRevision: 1, new MigrationOptions(), Ct);

        await world.Service.RollbackAsync(Project, targetRevision: 1, new MigrationOptions { AllowDestructive = true }, Ct);

        ShouldBeTheCheckedCatalog(world, binder, compiles);
    }

    /// <summary>
    /// The binder caches one binding per catalog instance, so binding the published catalog compiles nothing when it
    /// is the instance the guard checked — and compiles the handler's condition again when it is a second build.
    /// </summary>
    private static void ShouldBeTheCheckedCatalog(RuntimeSchemaWorld world, ExtensionBinder binder, CountingCompiler compiles)
    {
        var checkedCompiles = compiles.Calls;
        checkedCompiles.ShouldBeGreaterThan(0, "the guard binds the proposed catalog, compiling the handler's condition");

        binder.Result(world.PolicyCatalogs.Current.ShouldNotBeNull());

        compiles.Calls.ShouldBe(checkedCompiles, "the published catalog is the instance the guard checked, not a second build");
    }

    private static (Func<IPolicyCatalogProvider, ExtensionApplyGuard> Guard, ExtensionBinder Binder, CountingCompiler Compiles) Watched()
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tasks", tasks => tasks.OnUpdated("on-notes", HandlerCatalog.OnUpdated, when: "changed(notes)")));
        var compiles = new CountingCompiler(new CelCompiler());
        var binder = new ExtensionBinder(registry, compiles, HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);
        return (catalogs => new ExtensionApplyGuard(registry, binder, new CelCompiler(), catalogs), binder, compiles);
    }

    private static async Task<RuntimeSchemaWorld> WithNotesAsync()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);
        await world.Service.ApplyAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct);
        world.Writer.Forget();
        world.PolicyCatalogs.Forget();

        return world;
    }

    private static string Pointed(DescriptorValidationException refused) =>
        refused.Result.Errors.Where(error => error.Path.Length > 0).ShouldHaveSingleItem().Path;

    private static PolicyCatalog CatalogOf(string descriptorJson)
    {
        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }

    /// <summary>Counts compilations, forwarding each to the real compiler.</summary>
    /// <param name="inner">The compiler that actually compiles.</param>
    private sealed class CountingCompiler(ICelCompiler inner) : ICelCompiler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity)
        {
            Interlocked.Increment(ref _calls);
            return inner.Compile(source, profile, entity);
        }
    }
}
