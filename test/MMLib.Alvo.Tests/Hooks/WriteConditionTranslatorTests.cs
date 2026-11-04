using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>A typed write condition translates to the CEL a typed event condition does (Ruling 25, HX-08 Ruling 2's precedent).</summary>
public sealed class WriteConditionTranslatorTests
{
    [Fact]
    public void New_old_changed_and_the_caller_translate_as_an_event_condition_does()
    {
        WriteConditionTranslator.Translate<HookBooking>(w => w.Changed(x => x.Nights) && w.New.Nights > w.Old.Nights && w.Caller.HasRole("staff"), "guard")
            .ShouldBe(ConditionTranslator.Translate<HookBooking>(e => e.Changed(x => x.Nights) && e.New.Nights > e.Old.Nights && e.Caller.HasRole("staff"), "guard", HandlerVerb.Updated));
    }

    [Fact]
    public void An_untranslatable_condition_is_refused_naming_the_hook()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => WriteConditionTranslator.Translate<HookBooking>(w => w.New.Guest.GetHashCode() == 1, "guard"));

        refusal.Message.ShouldContain("'guard'");
    }
}
