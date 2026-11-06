using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Reads;

using System.Net.Http;

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

    [Fact]
    public async Task A_write_hook_injecting_an_http_client_refuses_boot_naming_the_parameter()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(alvo =>
        {
            alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "hook-desk.alvo.json"));
            alvo.Entity("bookings", b => b.BeforeWrite("network-guard", (BeforeWrite<AlvoRecord> _, HttpClient http) => ValueTask.CompletedTask));
        }, cancellationToken: TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("'network-guard'");
        refusal.Message.ShouldContain("extensions/network-guard/dependencies");
        refusal.Message.ShouldContain("'http'");
        refusal.Message.ShouldNotContain("/provider", customMessage: "the real SQLite composition is relational");
    }

    [Fact]
    public async Task A_narrowing_depending_on_IAlvoData_refuses_boot_naming_the_parameter()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(alvo =>
        {
            alvo.FromDescriptor(ReadsDesk);
            alvo.Entity("notes", n => n.Narrow<DataReadingNarrowing>("peeking-narrowing"));
        }, cancellationToken: Ct));

        refusal.Message.ShouldContain("extensions/peeking-narrowing/dependencies");
        refusal.Message.ShouldContain("'data'");
    }

    [Fact]
    public async Task A_narrowing_on_every_entity_refuses_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(alvo =>
        {
            alvo.FromDescriptor(ReadsDesk);
            alvo.Entity("*", every => every.Narrow("everywhere", (QueryNarrowing _) => null));
        }, cancellationToken: Ct));

        refusal.Message.ShouldContain("'everywhere' (narrowing) at extensions/everywhere/entity");
    }

    [Fact]
    public async Task A_narrowing_on_an_unknown_entity_refuses_boot_with_a_suggestion()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(alvo =>
        {
            alvo.FromDescriptor(ReadsDesk);
            alvo.Entity("note", n => n.Narrow("misspelled-narrowing", (QueryNarrowing _) => null));
        }, cancellationToken: Ct));

        refusal.Message.ShouldContain("'misspelled-narrowing' (narrowing) at extensions/misspelled-narrowing/entity");
        refusal.FixSuggestion.Split(Environment.NewLine)
            .Single(line => line.StartsWith("'misspelled-narrowing':", StringComparison.Ordinal))
            .ShouldContain("Did you mean 'notes'?");
    }

    [Fact]
    public async Task An_enricher_depending_on_IAlvoData_refuses_boot_naming_the_parameter()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(alvo =>
        {
            alvo.FromDescriptor(ReadsDesk);
            alvo.Entity("notes", n => n.Enrich<DataReadingEnricher>("peeking-enricher"));
        }, cancellationToken: Ct));

        refusal.Message.ShouldContain("'peeking-enricher'");
        refusal.Message.ShouldContain("extensions/peeking-enricher/dependencies");
    }

    [Fact]
    public async Task An_enricher_on_every_entity_refuses_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(alvo =>
        {
            alvo.FromDescriptor(ReadsDesk);
            alvo.Entity("*", every => every.Enrich("everywhere", (Enrich<AlvoRecord> _) => { }));
        }, cancellationToken: Ct));

        refusal.Message.ShouldContain("'everywhere' (enricher) at extensions/everywhere/entity");
    }

    private static string ReadsDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "reads-desk.alvo.json");
}

public sealed class DataReadingNarrowing(IAlvoData data) : IQueryNarrowing
{
    public AlvoFilter? Narrow(QueryNarrowing query)
    {
        _ = data;
        return null;
    }
}

public sealed class DataReadingEnricher(IAlvoData data) : IRecordEnricher<AlvoRecord>
{
    public void Enrich(Enrich<AlvoRecord> row) => row.SetVirtual("display", data.GetType().Name);
}
