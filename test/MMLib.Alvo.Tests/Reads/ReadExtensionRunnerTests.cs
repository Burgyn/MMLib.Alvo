using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Reads;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Tests.Reads;

/// <summary>
/// The read runner, the fail-closed core of the narrowing: what it ANDs, whom it skips, and how every defect of host code
/// fails the read with the extension's id and never its text.
/// </summary>
public sealed class ReadExtensionRunnerTests
{
    private static readonly AlvoComparison _north = new("region", AlvoFilterOperator.Eq, "north");
    private static readonly AlvoComparison _titled = new("title", AlvoFilterOperator.Eq, "a");

    [Fact]
    public async Task Two_narrowings_on_one_entity_are_anded_in_registration_order()
    {
        await using var world = ReadsWorld.Start(alvo => alvo
            .Entity("notes", n => n.Narrow("first", (QueryNarrowing _) => _north))
            .Entity("tickets", t => t.Narrow("elsewhere", (QueryNarrowing _) => _titled))
            .Entity("notes", n => n.Narrow("second", (QueryNarrowing _) => _titled)));

        var narrowing = (await world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct)).ShouldNotBeNull();

        narrowing.Filter.ShouldBeOfType<AlvoAnd>().Filters.ShouldBe([_north, _titled]);
        narrowing.ExtensionIds.ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task One_narrowing_is_its_own_filter_and_the_read_is_handed_to_it()
    {
        QueryNarrowing? seen = null;
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("only", (QueryNarrowing query) => { seen = query; return _north; })));

        (await world.Runner.NarrowingAsync("notes", DataOperation.Get, ReadsWorld.Caller, Ct)).ShouldNotBeNull().Filter.ShouldBe(_north);

        (seen!.Entity, seen.Operation, seen.Caller).ShouldBe(("notes", DataOperation.Get, ReadsWorld.Caller));
    }

    [Fact]
    public async Task A_system_caller_is_never_narrowed()
    {
        var asked = 0;
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("counted", (QueryNarrowing _) => { asked++; return _north; })));

        (await world.Runner.NarrowingAsync("notes", DataOperation.List, AlvoContext.System(ReadsWorld.Caller.Tenant), Ct)).ShouldBeNull();
        asked.ShouldBe(0);
    }

    [Fact]
    public async Task An_entity_no_narrowing_covers_is_not_narrowed()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("notes-only", (QueryNarrowing _) => _north)));

        (await world.Runner.NarrowingAsync("tickets", DataOperation.List, ReadsWorld.Caller, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_narrowing_returning_null_leaves_the_read_as_the_rules_made_it()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n
            .Narrow("nothing", (QueryNarrowing _) => null)
            .Narrow("something", (QueryNarrowing _) => _north)));

        var narrowing = (await world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct)).ShouldNotBeNull();

        narrowing.Filter.ShouldBe(_north);
        narrowing.ExtensionIds.ShouldBe(["nothing", "something"]);
    }

    [Fact]
    public async Task Only_null_narrowings_narrow_nothing()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("nothing", (QueryNarrowing _) => null)));

        (await world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_throwing_narrowing_fails_the_read_with_its_id_and_never_its_message()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow(
            "broken", (QueryNarrowing _) => throw new InvalidOperationException("secret host detail"))));

        var failure = (await FailureAsync(world));

        (failure.Extension, failure.Reason, failure.Stage).ShouldBe(("broken", HookFailedException.Exception, HookFailureStage.Read));
        failure.Message.ShouldBe("The read extension 'broken' failed (exception), so the read answered nothing.");
        world.Logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("broken", StringComparison.Ordinal));
        world.Logs.Entries.ShouldAllBe(entry => !entry.Message.Contains("secret host detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_untranslatable_typed_narrowing_is_reason_translation()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity<ReadNote>(n => n.Narrow("hashed", _ => note => note.Title.GetHashCode() == 3)));

        (await FailureAsync(world)).Reason.ShouldBe(HookFailedException.Translation);
    }

    [Fact]
    public async Task A_typed_narrowing_is_translated_on_every_read()
    {
        var region = "north";
        await using var world = ReadsWorld.Start(alvo => alvo.Entity<ReadNote>(n => n.Narrow("by-region", _ => note => note.Region == region)));

        (await world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct))!.Filter.ShouldBe(_north);
        region = "south";
        (await world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct))!.Filter.ShouldBe(new AlvoComparison("region", AlvoFilterOperator.Eq, "south"));
    }

    [Fact]
    public async Task A_narrowing_over_an_undeclared_field_is_invalid_filter()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow(
            "typo", (QueryNarrowing _) => new AlvoComparison("regoin", AlvoFilterOperator.Eq, "north"))));

        (await FailureAsync(world)).Reason.ShouldBe(HookFailedException.InvalidFilter);
    }

    [Fact]
    public async Task A_narrowing_over_a_virtual_field_is_invalid_filter()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow(
            "by-display", (QueryNarrowing _) => new AlvoComparison("display", AlvoFilterOperator.Eq, "x"))));

        (await FailureAsync(world)).Reason.ShouldBe(HookFailedException.InvalidFilter);
    }

    [Fact]
    public async Task A_narrowing_over_a_framework_column_is_allowed()
    {
        var id = new AlvoComparison("id", AlvoFilterOperator.Eq, Guid.Empty);
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("by-id", (QueryNarrowing _) => id)));

        (await world.Runner.NarrowingAsync("notes", DataOperation.Get, ReadsWorld.Caller, Ct))!.Filter.ShouldBe(id);
    }

    [Fact]
    public async Task A_narrowing_past_the_filter_depth_is_invalid_filter()
    {
        AlvoFilter deep = _north;
        for (var level = 0; level < AlvoFilter.MaxDepth + 1; level++)
        {
            deep = new AlvoNot(deep);
        }

        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("deep", (QueryNarrowing _) => deep)));

        (await FailureAsync(world)).Reason.ShouldBe(HookFailedException.InvalidFilter);
    }

    [Fact]
    public async Task A_registered_narrowing_the_catalog_does_not_bind_fails_closed()
    {
        var withoutNotes = ReadsWorld.Catalog("""
            { "apiVersion": "alvo.dev/v1", "name": "reads-desk", "entities": { "tickets": { "fields": { "title": { "type": "string" } } } } }
            """);
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("orphan", (QueryNarrowing _) => _north)), withoutNotes);

        (await FailureAsync(world)).Reason.ShouldBe(HookFailedException.Unbound);
    }

    [Fact]
    public async Task A_rules_only_read_is_never_narrowed()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("north", (QueryNarrowing _) => _north)));

        using (AlvoFlow.EnterRulesOnlyRead())
        {
            (await world.Runner.NarrowingAsync("notes", DataOperation.Get, ReadsWorld.Caller, Ct)).ShouldBeNull();
        }

        (await world.Runner.NarrowingAsync("notes", DataOperation.Get, ReadsWorld.Caller, Ct)).ShouldNotBeNull("the scope ends with its disposal");
    }

    [Fact]
    public async Task A_delegate_narrowing_opens_no_scope()
    {
        var scopes = new CountingScopeFactory();
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("north", (QueryNarrowing _) => _north)));

        (await world.RunnerOver(scopes).NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct)).ShouldNotBeNull();

        scopes.Created.ShouldBe(0);
    }

    [Fact]
    public async Task A_narrowing_naming_a_hidden_field_warns_once()
    {
        var salary = new AlvoComparison("salary", AlvoFilterOperator.Lt, 1000L);
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("by-salary", (QueryNarrowing _) => salary)));

        (await world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct)).ShouldNotBeNull().Filter.ShouldBe(salary);
        _ = await world.Runner.NarrowingAsync("notes", DataOperation.Get, ReadsWorld.Caller, Ct);

        world.Logs.Warnings.ShouldHaveSingleItem().ShouldContain("by-salary");
        world.Logs.Warnings[0].ShouldContain("salary");
    }

    [Fact]
    public async Task A_failed_narrowing_inside_a_unit_blames_the_unit()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow(
            "broken", (QueryNarrowing _) => throw new InvalidOperationException("no"))));
        var unit = new FakeFlowUnit();

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            var failure = (await FailureAsync(world));
            unit.Blamed.ShouldHaveSingleItem().ShouldBeSameAs(failure);
        }
    }

    [Fact]
    public async Task A_class_narrowing_is_built_in_one_scope_per_read_and_the_scope_is_disposed()
    {
        await using var world = ReadsWorld.Start(
            alvo => alvo.Entity("notes", n => n.Narrow<AsyncServiceNarrowing>("async-service")),
            services: services => services.AddScoped<AsyncOnlyService>());
        var scopes = world.CountingScopes();

        var narrowing = await world.RunnerOver(scopes).NarrowingAsync("notes", DataOperation.Get, ReadsWorld.Caller, Ct);

        narrowing.ShouldNotBeNull().Filter.ShouldBe(_north);
        scopes.Created.ShouldBe(1);
        AsyncServiceNarrowing.LastService.ShouldNotBeNull().Disposed.ShouldBeTrue(
            "a scoped service that is only IAsyncDisposable is disposed with the read's scope, never refused by a synchronous Dispose");
    }

    [Fact]
    public async Task A_class_narrowing_that_is_only_async_disposable_is_disposed_after_the_call()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow<AsyncDisposableNarrowing>("async-disposable")));
        var before = AsyncDisposableNarrowing.Disposed;

        (await world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct)).ShouldNotBeNull();

        AsyncDisposableNarrowing.Disposed.ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_scope_whose_disposal_throws_fails_the_read_with_the_class_ids_and_blames_the_unit()
    {
        await using var world = ReadsWorld.Start(
            alvo => alvo.Entity("notes", n => n
                .Narrow("by-delegate", (QueryNarrowing _) => _north)
                .Narrow<ThrowingDisposalNarrowing>("by-class")),
            services: services => services.AddScoped<ThrowingDisposalService>());
        var unit = new FakeFlowUnit();

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            var failure = await FailureAsync(world);

            (failure.Reason, failure.Extensions.Single()).ShouldBe((HookFailedException.Exception, "by-class"));
            unit.Blamed.ShouldHaveSingleItem().ShouldBeSameAs(failure);
        }

        world.Logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("by-class", StringComparison.Ordinal));
        world.Logs.Entries.ShouldAllBe(entry => !entry.Message.Contains("disposal secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_the_driver_finds_is_logged_once_with_the_ids_and_blames_the_unit()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Narrow("typed-wrong", (QueryNarrowing _) => _north)));
        var unit = new FakeFlowUnit();

        HookFailedException failure;
        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            failure = world.Runner.Refused(["typed-wrong", "other"], HookFailedException.InvalidFilter, new ArgumentException("secret value 2020"));
        }

        (failure.Stage, failure.Reason).ShouldBe((HookFailureStage.Read, HookFailedException.InvalidFilter));
        failure.Extensions.ShouldBe(["typed-wrong", "other"]);
        unit.Blamed.ShouldHaveSingleItem().ShouldBeSameAs(failure);
        var error = world.Logs.Entries.Where(entry => entry.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain("typed-wrong");
        error.Message.ShouldNotContain("secret value 2020");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HookFailedException> FailureAsync(ReadsWorld world) =>
        Should.ThrowAsync<HookFailedException>(() => world.Runner.NarrowingAsync("notes", DataOperation.List, ReadsWorld.Caller, Ct).AsTask());

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        internal int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;
            throw new InvalidOperationException("a delegate narrowing takes no service");
        }
    }
}

public sealed class AsyncOnlyService : IAsyncDisposable
{
    internal bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class AsyncServiceNarrowing(AsyncOnlyService service) : IQueryNarrowing
{
    internal static AsyncOnlyService? LastService { get; private set; }

    public AlvoFilter? Narrow(QueryNarrowing query)
    {
        LastService = service;
        return new AlvoComparison("region", AlvoFilterOperator.Eq, "north");
    }
}

public sealed class AsyncDisposableNarrowing : IQueryNarrowing, IAsyncDisposable
{
    internal static int Disposed { get; private set; }

    public AlvoFilter? Narrow(QueryNarrowing query) => new AlvoComparison("region", AlvoFilterOperator.Eq, "north");

    public ValueTask DisposeAsync()
    {
        Disposed++;
        return ValueTask.CompletedTask;
    }
}

public sealed class ThrowingDisposalService : IAsyncDisposable
{
    public ValueTask DisposeAsync() => throw new InvalidOperationException("disposal secret");
}

public sealed class ThrowingDisposalNarrowing(ThrowingDisposalService service) : IQueryNarrowing
{
    public AlvoFilter? Narrow(QueryNarrowing query)
    {
        _ = service;
        return null;
    }
}
