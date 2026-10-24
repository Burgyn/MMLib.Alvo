using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// A host whose handler registrations do not fit its descriptor never starts (spec 2026-10-14 §7 HX-03 acceptance (7),
/// (14), (15)): one refusal, every problem in it, each naming the extension.
/// </summary>
public sealed class ExtensionBootRefusalTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_when_typo_refuses_the_boot_naming_the_id_beside_every_other_problem()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(
            alvo => alvo.FromDescriptor(HelpDesk)
                .Entity("tickets", t => t.OnUpdated("ticket-closed", (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask, when: "new.stauts == 'closed'"))
                .Entity("tikets", t => t.OnCreated("misspelled-entity", (AlvoCreated<AlvoRecord> _) => Task.CompletedTask)),
            cancellationToken: Ct));

        refusal.Message.ShouldContain("'ticket-closed'");
        refusal.Message.ShouldContain("extensions/ticket-closed/when");
        refusal.Message.ShouldContain("'misspelled-entity'");
        refusal.FixSuggestion.ShouldContain("Did you mean 'tickets'?");
    }

    [Fact]
    public async Task A_star_handler_whose_condition_reads_a_field_refuses_the_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(
            alvo => alvo.FromDescriptor(HelpDesk)
                .Entity("*", all => all.OnSaved("audit-all", (AlvoSaved<AlvoRecord> _) => Task.CompletedTask, when: "new.title == 'x'")),
            cancellationToken: Ct));

        refusal.Message.ShouldContain("extensions/audit-all/when");
    }

    [Fact]
    public async Task Two_ids_deriving_one_event_name_refuse_the_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(
            alvo => alvo.FromDescriptor(HelpDesk)
                .Entity("tickets", t => t.OnCreated("a-b", (AlvoCreated<AlvoRecord> _) => Task.CompletedTask))
                .AddRetiredExtension("a_b", AlvoRetirement.Discard),
            cancellationToken: Ct));

        refusal.Message.ShouldContain("'a-b' and 'a_b'");
    }
}
