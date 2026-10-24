using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// The apply guard (spec 2026-10-14 §4.2, HX-03 settled details (6)): a change is refused when a handler that binds the
/// current catalog would no longer bind the proposed one, each finding at the pointer of what the change removed or
/// excluded, beside the compiler's own sentence; a handler that binds neither is a warning, never a refusal.
/// </summary>
public sealed class ExtensionApplyGuardTests
{
    private const string FieldFix = "Remove the registration and redeploy first, or keep the field.";

    [Fact]
    public void A_catalog_every_bound_handler_still_fits_passes_and_is_handed_back()
    {
        var proposed = CatalogOf(WithField("tickets", "priority"));

        var check = Guard(OnStatus("on-status")).Check(proposed);

        check.Catalog.ShouldBeSameAs(proposed, "the apply publishes the catalog the guard checked, never a second build");
        check.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Removing_a_field_a_bound_handler_reads_is_refused_at_the_field_naming_the_extension()
    {
        var errors = Refused(Guard(OnStatus("on-status")), WithoutField("tickets", "status"));

        var precise = Precise(errors).ShouldHaveSingleItem();
        precise.Path.ShouldBe("/entities/tickets/fields/status");
        precise.Message.ShouldContain("'on-status' (eventHandler, in this host)");
        precise.FixSuggestion.ShouldBe(FieldFix);
        errors.ShouldAllBe(error => error.Severity == DescriptorValidationSeverity.Error);
        errors.ShouldContain(error => error.Path.Length == 0, "the compiler's own sentence is kept beside the pointer");
    }

    [Fact]
    public void Renaming_a_field_a_bound_handler_reads_is_refused_at_the_old_field()
    {
        var renamed = Replaced("tickets", entity => entity with
        {
            Fields = [.. entity.Fields.Select(field => field.Name == "status" ? field with { Name = "state" } : field)],
        });

        Precise(Refused(Guard(OnStatus("on-status")), renamed)).ShouldHaveSingleItem().Path.ShouldBe("/entities/tickets/fields/status");
    }

    [Fact]
    public void Removing_an_entity_a_bound_handler_names_is_refused_at_the_entity()
    {
        var withoutTickets = new SchemaModel([.. HandlerCatalog.Schema.Entities.Where(entity => entity.Name != "tickets")]);

        var precise = Precise(Refused(Guard(OnStatus("on-status")), withoutTickets)).ShouldHaveSingleItem();

        precise.Path.ShouldBe("/entities/tickets");
        precise.FixSuggestion.ShouldBe("Remove the registration and redeploy first, or keep the entity.");
    }

    [Fact]
    public void Excluding_a_field_a_bound_handler_reads_is_refused_at_the_exclusion()
    {
        var excluded = Replaced("tickets", entity => entity with { EventImageExclusions = ["secret", "status"] });

        var precise = Precise(Refused(Guard(OnStatus("on-status")), excluded)).ShouldHaveSingleItem();

        precise.Path.ShouldBe("/entities/tickets/events/exclude/1");
        precise.FixSuggestion.ShouldBe("Remove the registration and redeploy first, or keep the field in the event images.");
    }

    [Fact]
    public void Every_bound_handler_a_change_unbinds_is_named_on_its_own()
    {
        var guard = Guard(alvo =>
        {
            OnStatus("first")(alvo);
            OnStatus("second")(alvo);
        });

        var messages = Precise(Refused(guard, WithoutField("tickets", "status"))).Select(error => error.Message).ToList();

        messages.Count.ShouldBe(2);
        messages.ShouldContain(message => message.Contains("'first'", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("'second'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_retyped_field_the_condition_no_longer_compiles_against_is_refused_at_the_field()
    {
        var guard = Guard(alvo => alvo.Entity("tickets", t => t.OnUpdated("on-resolved", OnUpdated, when: "new.status == 'resolved'")));
        var retyped = Replaced("tickets", entity => entity with
        {
            Fields = [.. entity.Fields.Select(field => field.Name == "status" ? new FieldSchema { Name = "status", Type = FieldType.Integer } : field)],
        });

        var errors = Refused(guard, retyped);

        var precise = Precise(errors).ShouldHaveSingleItem();
        precise.Path.ShouldBe("/entities/tickets/fields/status");
        precise.Message.ShouldContain("'on-resolved' (eventHandler, in this host)");
        precise.FixSuggestion.ShouldBe("Remove the registration and redeploy first, or keep the field's type.");
        errors.ShouldContain(error => error.Path.Length == 0 && error.Message.Contains("'on-resolved' (eventHandler, in this host) would no longer bind"));
    }

    [Fact]
    public void A_retyped_field_the_condition_still_compiles_against_gets_no_pointer_when_another_cause_refuses()
    {
        var guard = Guard(alvo => alvo.Entity("tickets", t => t.OnUpdated("on-both", OnUpdated, when: "changed(code) && changed(status)")));
        var changed = Replaced("tickets", entity => entity with
        {
            Fields = [.. entity.Fields
                .Where(field => field.Name != "status")
                .Select(field => field.Name == "code" ? new FieldSchema { Name = "code", Type = FieldType.Integer, Nullable = true } : field)],
        });

        Precise(Refused(guard, changed)).ShouldHaveSingleItem().Path.ShouldBe("/entities/tickets/fields/status");
    }

    [Fact]
    public void A_refusal_carries_the_warnings_of_a_handler_that_was_already_unbound()
    {
        var withoutCode = WithoutField("tickets", "code");
        var guard = Guard(
            alvo =>
            {
                alvo.Entity("tickets", t => t.OnUpdated("stale", OnUpdated, when: "changed(code)"));
                OnStatus("on-status")(alvo);
            },
            current: CatalogOf(withoutCode));
        var proposed = new SchemaModel([.. withoutCode.Entities.Select(entity => entity.Name == "tickets"
            ? entity with { Fields = [.. entity.Fields.Where(field => field.Name != "status")] }
            : entity)]);

        var errors = Refused(guard, proposed);

        Precise(errors).ShouldHaveSingleItem().Message.ShouldContain("'on-status'");
        errors.Where(error => error.Severity == DescriptorValidationSeverity.Error).ShouldAllBe(error => !error.Message.Contains("'stale'"));
        errors.ShouldContain(error => error.Severity == DescriptorValidationSeverity.Warning && error.Message.Contains("'stale'"));
    }

    [Fact]
    public void With_nothing_primed_an_unfit_handler_is_a_warning_not_a_refusal()
    {
        var check = Guard(OnStatus("on-status"), current: null).Check(CatalogOf(WithoutField("tickets", "status")));

        check.Warnings.ShouldNotBeEmpty();
        check.Warnings.ShouldAllBe(warning => warning.Severity == DescriptorValidationSeverity.Warning);
        check.Warnings.ShouldAllBe(warning => warning.Message.Contains("'on-status'"));
    }

    [Fact]
    public void A_handler_already_unbound_does_not_block_an_unrelated_change()
    {
        var unbound = WithoutField("tickets", "status");
        var guard = Guard(OnStatus("on-status"), current: CatalogOf(unbound));

        var check = guard.Check(CatalogOf(new SchemaModel([.. unbound.Entities.Where(entity => entity.Name != "notes")])));

        check.Warnings.ShouldContain(warning => warning.Message.Contains("'on-status'"), "still reported, as it was before the change");
    }

    [Fact]
    public void An_every_entity_or_application_event_handler_never_refuses_an_apply()
    {
        var guard = Guard(alvo => alvo
            .Entity("*", every => every.OnSaved("audit", OnSaved, when: "'admin' in @user.roles"))
            .AddEventHandler("approved", "orders.approved", OnRecord));
        var withoutNotes = new SchemaModel([.. HandlerCatalog.Schema.Entities.Where(entity => entity.Name != "notes")]);

        guard.Check(CatalogOf(withoutNotes)).Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_host_without_handlers_builds_no_catalog_and_checks_nothing()
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "handlers",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal),
        };

        Guard(_ => { }).Check(descriptor, new SchemaModel([])).ShouldBeNull();
    }

    private static Action<IAlvoBuilder> OnStatus(string id) =>
        alvo => alvo.Entity("tickets", t => t.OnUpdated(id, OnUpdated, when: "changed(status)"));

    private static ExtensionApplyGuard Guard(Action<IAlvoBuilder> configure) => Guard(configure, Catalog());

    private static ExtensionApplyGuard Guard(Action<IAlvoBuilder> configure, PolicyCatalog? current)
    {
        var catalogs = new PolicyCatalogProvider();
        if (current is not null)
        {
            catalogs.SetCurrent("handlers", current);
        }

        var registry = Registry(configure);
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, EveryService(), NullLogger<ExtensionBinder>.Instance);
        return new ExtensionApplyGuard(registry, binder, CelFixtures.Compiler, catalogs);
    }

    private static IReadOnlyList<DescriptorValidationError> Refused(ExtensionApplyGuard guard, SchemaModel proposed) =>
        Should.Throw<DescriptorValidationException>(() => guard.Check(CatalogOf(proposed))).Result.Errors;

    private static IEnumerable<DescriptorValidationError> Precise(IEnumerable<DescriptorValidationError> errors) =>
        errors.Where(error => error.Path.Length > 0);

    private static SchemaModel Replaced(string name, Func<EntitySchema, EntitySchema> change) =>
        new([.. HandlerCatalog.Schema.Entities.Select(entity => entity.Name == name ? change(entity) : entity)]);

    private static SchemaModel WithoutField(string entity, string field) =>
        Replaced(entity, schema => schema with { Fields = [.. schema.Fields.Where(candidate => candidate.Name != field)] });

    private static SchemaModel WithField(string entity, string field) =>
        Replaced(entity, schema => schema with { Fields = [.. schema.Fields, new FieldSchema { Name = field, Type = FieldType.String, Nullable = true }] });
}
