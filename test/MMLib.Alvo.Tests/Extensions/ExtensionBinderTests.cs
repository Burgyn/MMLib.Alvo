using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>The binder: strict at boot, lenient — and loud — on a catalog a runtime apply primed later.</summary>
public sealed class ExtensionBinderTests : IDisposable
{
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;

    public ExtensionBinderTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public void The_boot_is_refused_with_every_problem_and_its_fix()
    {
        var binder = Binder(alvo => alvo
            .Entity("tickets", t => t.OnUpdated("typo", OnUpdated, when: "new.stauts == 'x'"))
            .Entity("tikets", t => t.OnCreated("misspelled", OnCreated)));

        var refusal = Should.Throw<AlvoStartupRefusedException>(() => binder.EnsureBindable(Catalog()));

        refusal.Message.ShouldContain("'typo' (eventHandler) at extensions/typo/when");
        refusal.Message.ShouldContain("'misspelled' (eventHandler) at extensions/misspelled/entity");
        refusal.FixSuggestion.ShouldContain("Did you mean 'tickets'?");
    }

    [Fact]
    public void A_tie_is_logged_at_boot_by_name()
    {
        var binder = Binder(alvo => alvo.Entity("tickets", t => t
            .OnCreated("first", OnCreated, configure: o => o.Order = 1)
            .OnCreated("second", OnCreated, configure: o => o.Order = 1)));

        binder.EnsureRegistrable();
        binder.EnsureBindable(Catalog());

        _logs.Entries.Where(entry => entry.EventName == "ExtensionOrderTie").ShouldHaveSingleItem().Message.ShouldContain("first, second");
    }

    [Fact]
    public void A_registration_no_descriptor_can_cure_is_refused_without_a_catalog()
    {
        var binder = Binder(alvo => alvo.Entity("*", all => all.OnSaved("audit-all", OnSaved, when: "new.title == 'x'")));

        Should.Throw<AlvoStartupRefusedException>(binder.EnsureRegistrable).Message.ShouldContain("extensions/audit-all/when");
    }

    [Fact]
    public void A_registration_with_only_catalog_problems_passes_the_catalog_free_check() =>
        Should.NotThrow(Binder(alvo => alvo.Entity("tikets", t => t.OnCreated("misspelled", OnCreated))).EnsureRegistrable);

    [Fact]
    public void One_catalog_is_bound_once()
    {
        var binder = Binder(alvo => alvo.Entity("tickets", t => t.OnCreated("first", OnCreated)));
        var catalog = Catalog();

        binder.For(catalog).ShouldBeSameAs(binder.For(catalog));
    }

    [Fact]
    public void A_catalog_that_unbinds_a_handler_logs_it_and_binds_the_rest()
    {
        var binder = Binder(alvo => alvo
            .Entity("tickets", t => t.OnCreated("on-tickets", OnCreated))
            .Entity("notes", n => n.OnCreated("on-notes", OnCreated)));
        var withoutNotes = PolicyCatalog.Build(
            new() { ApiVersion = "alvo.dev/v1", Name = "handlers", Entities = new Dictionary<string, MMLib.Alvo.Descriptor.EntityDescriptor>(StringComparer.Ordinal) },
            new SchemaModel([HandlerCatalog.Schema.Entities.Single(entity => entity.Name == "tickets")]),
            CelFixtures.Compiler);

        var bindings = binder.For(withoutNotes);
        binder.For(withoutNotes);

        bindings.Handlers.Select(handler => handler.Id).ShouldBe(["on-tickets"]);
        bindings.Unbound.Select(handler => handler.Id).ShouldBe(["on-notes"], "kept, so its events are recorded dead rather than dropped");
        _logs.Entries.Count(entry => entry.EventName == "ExtensionUnbound" && entry.Level == LogLevel.Error).ShouldBe(1);
    }

    [Fact]
    public void A_handler_with_several_problems_is_logged_once_per_catalog_with_every_problem()
    {
        var binder = Binder(alvo => alvo.Entity("tickets", t => t.OnCreated("broken", OnCreated, when: "old.status == 'open'", configure: o => o.Timeout = TimeSpan.Zero)));

        binder.For(Catalog()).Unbound.Select(handler => handler.Id).ShouldBe(["broken"]);

        var line = _logs.Entries.Where(entry => entry.EventName == "ExtensionUnbound").ShouldHaveSingleItem();
        line.Message.ShouldContain("extensions/broken/when");
        line.Message.ShouldContain("extensions/broken/timeout");
    }

    [Fact]
    public void Each_new_catalog_is_bound_afresh_never_from_a_stale_one()
    {
        var binder = Binder(alvo => alvo.Entity("notes", n => n.OnCreated("on-notes", OnCreated)));
        var withoutNotes = PolicyCatalog.Build(
            new() { ApiVersion = "alvo.dev/v1", Name = "handlers", Entities = new Dictionary<string, MMLib.Alvo.Descriptor.EntityDescriptor>(StringComparer.Ordinal) },
            new SchemaModel([HandlerCatalog.Schema.Entities.Single(entity => entity.Name == "tickets")]),
            CelFixtures.Compiler);

        binder.For(Catalog()).Handlers.Select(handler => handler.Id).ShouldBe(["on-notes"]);
        binder.For(withoutNotes).Handlers.ShouldBeEmpty();
        binder.For(withoutNotes).Unbound.Select(handler => handler.Id).ShouldBe(["on-notes"]);
    }

    [Fact]
    public void A_catalog_bound_strictly_at_boot_is_not_logged_again()
    {
        var binder = Binder(alvo => alvo.Entity("tickets", t => t.OnCreated("first", OnCreated)));
        var catalog = Catalog();

        binder.EnsureBindable(catalog);
        binder.For(catalog).Handlers.ShouldHaveSingleItem();

        _logs.Entries.ShouldBeEmpty();
    }

    private ExtensionBinder Binder(Action<IAlvoBuilder> configure) =>
        new(Registry(configure), CelFixtures.Compiler, EveryService(), _loggers.CreateLogger<ExtensionBinder>());
}
