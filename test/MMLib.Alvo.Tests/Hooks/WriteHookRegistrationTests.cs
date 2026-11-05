using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Tests.Extensions;

namespace MMLib.Alvo.Tests.Hooks;

public sealed class VatIdIsRegistered : IValidateWriteHook
{
    public ValueTask ValidateAsync(ValidateWrite<AlvoRecord> write, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class TypedGuard : IBeforeWriteHook<HookBooking>
{
    public ValueTask BeforeWriteAsync(BeforeWrite<HookBooking> write, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class TypedTidy : IAfterDeleteHook<HookBooking>
{
    public ValueTask AfterDeleteAsync(AfterDelete<HookBooking> deletion, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public abstract class AbstractGuard : IBeforeWriteHook
{
    public abstract ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken);
}

/// <summary>What registering a write hook records (spec 2026-10-14 §4.2, §4.4): stage, entities, condition, the stage's defaults.</summary>
public sealed class WriteHookRegistrationTests
{
    [Fact]
    public void An_untyped_validate_hook_records_its_stage_entity_condition_and_the_validate_defaults()
    {
        var hook = HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.Validate<VatIdIsRegistered>("vat-in-vies", when: "changed(vat_id)")))
            .WriteHooks.ShouldHaveSingleItem();

        (hook.Id, hook.Stage, hook.When, hook.Settings.Timeout, hook.Settings.MaxOperations, hook.InTransaction)
            .ShouldBe(("vat-in-vies", WriteHookStage.Validate, "changed(vat_id)", TimeSpan.FromSeconds(5), 50, false));
        hook.Entities.ShouldBe(["bookings"]);
        hook.Invoker.TypeName.ShouldBe(typeof(VatIdIsRegistered).FullName);
    }

    [Fact]
    public void An_in_transaction_hook_defaults_to_a_quarter_second_and_its_options_are_kept()
    {
        var hook = HandlerCatalog.Registry(alvo => alvo.Entities(["bookings", "notes"], e => e.AfterWrite(
            "audit", (AfterWrite<AlvoRecord> _) => ValueTask.CompletedTask,
            configure: o => { o.Order = 2; o.MaxOperations = 7; o.Description = "Keeps an audit row."; }))).WriteHooks.ShouldHaveSingleItem();

        (hook.Stage, hook.Settings.Timeout, hook.Settings.Order, hook.Settings.OrderStated, hook.Settings.MaxOperations, hook.Settings.Description)
            .ShouldBe((WriteHookStage.AfterWrite, TimeSpan.FromMilliseconds(250), 2, true, 7, "Keeps an audit row."));
        hook.Entities.ShouldBe(["bookings", "notes"]);
        hook.Invoker.TypeName.ShouldBeNull();
    }

    [Fact]
    public void A_typed_condition_is_translated_to_cel_when_the_hook_is_registered()
    {
        var hook = HandlerCatalog.Registry(alvo => alvo.Entity<HookBooking>(b => b.BeforeWrite<TypedGuard>(
            "nights-guard", when: w => w.Changed(x => x.Nights) && w.New.Nights > 14))).WriteHooks.ShouldHaveSingleItem();

        hook.When.ShouldBe("changed(nights) && new.nights > 14");
        hook.Typed!.Type.ShouldBe(typeof(HookBooking));
    }

    [Fact]
    public void A_typed_delegate_s_condition_is_translated_to_cel_when_the_hook_is_registered()
    {
        var hook = HandlerCatalog.Registry(alvo => alvo.Entity<HookBooking>(b => b.AfterWrite(
            "nights-audit", (AfterWrite<HookBooking> _) => ValueTask.CompletedTask, w => w.New.Nights > 14))).WriteHooks.ShouldHaveSingleItem();

        (hook.Stage, hook.When, hook.Invoker.TypeName).ShouldBe((WriteHookStage.AfterWrite, "new.nights > 14", (string?)null));
    }

    [Fact]
    public void A_typed_delete_hook_s_condition_reading_the_new_image_is_refused_at_the_call()
    {
        Should.Throw<AlvoTranslationException>(() => HandlerCatalog.Registry(alvo => alvo.Entity<HookBooking>(b => b.BeforeDelete(
            "tidy", (BeforeDelete<HookBooking> _) => ValueTask.CompletedTask, w => w.New.Nights > 1))));
        Should.Throw<AlvoTranslationException>(() => HandlerCatalog.Registry(alvo => alvo.Entity<HookBooking>(b => b.AfterDelete<TypedTidy>(
            "tidy", w => w.New.Nights > 1))));
    }

    [Fact]
    public async Task A_delegate_parameter_the_context_is_an_instance_of_is_a_service_both_when_checked_and_when_called()
    {
        object? seen = null;
        var hook = HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.BeforeWrite(
            "loose", (BeforeWrite<AlvoRecord> _, object extra) => { seen = extra; }))).WriteHooks.ShouldHaveSingleItem();
        await using var services = new ServiceCollection().AddSingleton<object>("from-services").BuildServiceProvider();

        await hook.Invoker.InvokeAsync(services, HookContextTests.Run(AlvoWriteOperation.Create, null, HookContextTests.Unsaved("Ada", 2)), TestContext.Current.CancellationToken);

        (hook.Invoker.Dependencies.ShouldHaveSingleItem().Type, seen).ShouldBe((typeof(object), (object?)"from-services"));
    }

    [Theory]
    [InlineData(AlvoWriteOperation.Create, true, false)]
    [InlineData(AlvoWriteOperation.Update, true, false)]
    [InlineData(AlvoWriteOperation.Replace, true, false)]
    [InlineData(AlvoWriteOperation.Delete, false, true)]
    public void A_write_stage_runs_on_writes_and_a_delete_stage_on_deletes(AlvoWriteOperation operation, bool write, bool delete)
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("*", every => every
            .BeforeWrite("w", (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask)
            .BeforeDelete("d", (BeforeDelete<AlvoRecord> _) => ValueTask.CompletedTask)));

        registry.WriteHooks.Single(hook => hook.Id == "w").RunsOn(operation).ShouldBe(write);
        registry.WriteHooks.Single(hook => hook.Id == "d").RunsOn(operation).ShouldBe(delete);
        registry.WriteHooks.ShouldAllBe(hook => hook.IsEveryEntity && hook.Covers("anything"));
    }

    [Fact]
    public void A_delegate_whose_result_alvo_would_discard_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.BeforeWrite(
            "bad", (BeforeWrite<AlvoRecord> _) => 42)))).ParamName.ShouldBe("hook");

    [Fact]
    public void A_delegate_asking_for_another_stage_s_context_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.AfterWrite(
            "confused", (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask)))).Message.ShouldContain("AfterWrite<AlvoRecord>");

    [Fact]
    public void An_abstract_hook_class_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.BeforeWrite<AbstractGuard>("abstract"))));
}
