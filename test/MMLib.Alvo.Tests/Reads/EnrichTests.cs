using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Reads;
using MMLib.Alvo.Reads.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Tests.Reads;

[AlvoEntity("notes")]
public sealed class SalariedNote
{
    public Guid Id { get; init; }

    public string? Title { get; init; }

    public long Salary { get; init; }

    public string? Display { get; init; }
}

public sealed class ThrowingDisposalEnricher(ThrowingDisposalService service) : IRecordEnricher<AlvoRecord>
{
    public void Enrich(Enrich<AlvoRecord> row) => _ = service;
}

public sealed class TitleEnricher : IRecordEnricher<AlvoRecord>
{
    internal static int Built { get; set; }

    public TitleEnricher() => Built++;

    public void Enrich(Enrich<AlvoRecord> row) => row.SetVirtual("display", row.Row["title"]);
}

/// <summary>
/// What an enricher can do to a returned row — hide a field, fill a virtual one, nothing else — and how the runner fails
/// the read closed when it tries anything more, throws, or is not bound.
/// </summary>
public sealed class EnrichTests
{
    private static readonly EntitySchema _notes = ReadsWorld.Catalog().Schema.Entities.Single(entity => entity.Name == "notes") with { Audit = true };

    private static readonly Guid _id = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    [Fact]
    public void Hide_removes_a_field()
    {
        var row = Shaping();

        row.Hide("region");

        row.ToRecord().Values.ContainsKey("region").ShouldBeFalse();
        row.ToRecord()["title"].ShouldBe("a");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("tenant_id")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    public void Hide_of_a_framework_column_is_invalid_hide(string column) =>
        Should.Throw<ReadShapingRefusal>(() => Shaping().Hide(column)).Reason.ShouldBe(HookFailedException.InvalidHide);

    [Fact]
    public void SetVirtual_fills_a_carried_virtual_field()
    {
        var row = Shaping();

        row.SetVirtual("display", "a (north)");

        row.ToRecord()["display"].ShouldBe("a (north)");
    }

    [Fact]
    public void SetVirtual_of_a_stored_field_is_invalid_set() =>
        Should.Throw<ReadShapingRefusal>(() => Shaping().SetVirtual("title", "b")).Reason.ShouldBe(HookFailedException.InvalidSet);

    [Fact]
    public void SetVirtual_of_an_undeclared_field_is_invalid_set() =>
        Should.Throw<ReadShapingRefusal>(() => Shaping().SetVirtual("nickname", "b")).Reason.ShouldBe(HookFailedException.InvalidSet);

    [Fact]
    public void SetVirtual_of_a_value_its_type_cannot_hold_is_invalid_set() =>
        Should.Throw<ReadShapingRefusal>(() => Shaping().SetVirtual("display", 42)).Reason.ShouldBe(HookFailedException.InvalidSet);

    [Fact]
    public void SetVirtual_of_a_virtual_field_the_row_does_not_carry_is_a_no_op()
    {
        var row = Shaping(Without("display"));

        row.SetVirtual("display", "x");

        row.ToRecord().Values.ContainsKey("display").ShouldBeFalse();
    }

    [Fact]
    public void A_typed_row_reads_a_policy_hidden_property_as_its_default()
    {
        var enrich = new Enrich<SalariedNote>(Shaping(Without("salary")), ReadsWorld.Caller, DataOperation.Get);

        enrich.Row.Salary.ShouldBe(0);
        enrich.Row.Title.ShouldBe("a");
    }

    [Fact]
    public void A_typed_SetVirtual_fills_the_field_its_property_maps()
    {
        var row = Shaping();

        new Enrich<SalariedNote>(row, ReadsWorld.Caller, DataOperation.List).SetVirtual(note => note.Display, "typed");
        new Enrich<SalariedNote>(row, ReadsWorld.Caller, DataOperation.List).Hide(note => note.Title);

        row.ToRecord()["display"].ShouldBe("typed");
        row.ToRecord().Values.ContainsKey("title").ShouldBeFalse();
    }

    [Fact]
    public async Task The_second_enricher_sees_what_the_first_did()
    {
        object? seen = "unset";
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n
            .Enrich("first", (Enrich<AlvoRecord> row) => row.SetVirtual("display", "from first"))
            .Enrich("second", (Enrich<AlvoRecord> row) => seen = row.Row["display"])));

        await using var enrichment = world.Runner.BeginEnrichment("notes", DataOperation.List, ReadsWorld.Caller).ShouldNotBeNull();
        enrichment.Enrich(Row())["display"].ShouldBe("from first");

        seen.ShouldBe("from first");
    }

    [Fact]
    public async Task An_enricher_is_handed_the_caller_the_entity_and_the_operation()
    {
        (AlvoContext Caller, string Entity, DataOperation Operation)? seen = null;
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n
            .Enrich("seeing", (Enrich<AlvoRecord> row) => seen = (row.Caller, row.Entity, row.Operation))));

        await using (var enrichment = world.Runner.BeginEnrichment("notes", DataOperation.Update, ReadsWorld.Caller))
        {
            enrichment.ShouldNotBeNull().Enrich(Row());
        }

        seen.ShouldBe((ReadsWorld.Caller, "notes", DataOperation.Update));
    }

    [Fact]
    public async Task An_entity_no_enricher_covers_begins_no_enrichment()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich("notes-only", (Enrich<AlvoRecord> _) => { })));

        world.Runner.BeginEnrichment("tickets", DataOperation.List, ReadsWorld.Caller).ShouldBeNull();
    }

    [Fact]
    public async Task A_system_read_is_enriched_too()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich("everyone", (Enrich<AlvoRecord> _) => { })));

        await using var enrichment = world.Runner.BeginEnrichment("notes", DataOperation.List, AlvoContext.System(ReadsWorld.Caller.Tenant));

        enrichment.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_rules_only_read_is_never_enriched()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich("everyone", (Enrich<AlvoRecord> _) => { })));

        using (AlvoFlow.EnterRulesOnlyRead())
        {
            world.Runner.BeginEnrichment("notes", DataOperation.Get, ReadsWorld.Caller).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_throwing_enricher_fails_the_read_with_its_id_and_never_its_message()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich(
            "boom", (Enrich<AlvoRecord> _) => throw new InvalidOperationException("secret host detail"))));

        var failure = await FailureAsync(world);

        (failure.Extension, failure.Reason, failure.Stage).ShouldBe(("boom", HookFailedException.Exception, HookFailureStage.Read));
        failure.Message.ShouldBe("The read extension 'boom' failed (exception), so the read answered nothing.");
        world.Logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("boom", StringComparison.Ordinal));
        world.Logs.Entries.ShouldAllBe(entry => !entry.Message.Contains("secret host detail", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hide-id", HookFailedException.InvalidHide)]
    [InlineData("set-title", HookFailedException.InvalidSet)]
    public async Task A_refused_move_fails_the_read_with_its_reason(string id, string reason)
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich(id, (Enrich<AlvoRecord> row) =>
        {
            if (id == "hide-id")
            {
                row.Hide("id");
            }
            else
            {
                row.SetVirtual("title", "b");
            }
        })));

        var failure = await FailureAsync(world);

        (failure.Extension, failure.Reason).ShouldBe((id, reason));
    }

    [Fact]
    public async Task A_registered_enricher_the_catalog_does_not_bind_fails_closed()
    {
        var withoutNotes = ReadsWorld.Catalog("""
            { "apiVersion": "alvo.dev/v1", "name": "reads-desk", "entities": { "tickets": { "fields": { "title": { "type": "string" } } } } }
            """);
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich("orphan", (Enrich<AlvoRecord> _) => { })), withoutNotes);

        var failure = Should.Throw<HookFailedException>(() => world.Runner.BeginEnrichment("notes", DataOperation.Get, ReadsWorld.Caller));

        (failure.Extension, failure.Reason).ShouldBe(("orphan", HookFailedException.Unbound));
    }

    [Fact]
    public async Task A_failed_enricher_inside_a_unit_blames_the_unit()
    {
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich(
            "boom", (Enrich<AlvoRecord> _) => throw new InvalidOperationException("no"))));
        var unit = new FakeFlowUnit();

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            var failure = await FailureAsync(world);
            unit.Blamed.ShouldHaveSingleItem().ShouldBeSameAs(failure);
        }
    }

    [Fact]
    public async Task A_delegate_enricher_opens_no_scope()
    {
        var scopes = new CountingScopeFactory();
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich("plain", (Enrich<AlvoRecord> _) => { })));

        await using var enrichment = world.RunnerOver(scopes).BeginEnrichment("notes", DataOperation.List, ReadsWorld.Caller);

        enrichment.ShouldNotBeNull().Enrich(Row());
        scopes.Created.ShouldBe(0);
    }

    [Fact]
    public async Task A_class_enricher_is_built_once_per_read_in_one_scope()
    {
        TitleEnricher.Built = 0;
        await using var world = ReadsWorld.Start(alvo => alvo.Entity("notes", n => n.Enrich<TitleEnricher>("by-class")));

        await using (var enrichment = world.Runner.BeginEnrichment("notes", DataOperation.List, ReadsWorld.Caller).ShouldNotBeNull())
        {
            enrichment.Enrich(Row())["display"].ShouldBe("a");
            enrichment.Enrich(Row().With("title", "b"))["display"].ShouldBe("b");
        }

        TitleEnricher.Built.ShouldBe(1);
    }

    [Fact]
    public async Task A_scope_whose_disposal_throws_fails_the_read_with_the_class_ids_and_blames_the_unit()
    {
        await using var world = ReadsWorld.Start(
            alvo => alvo.Entity("notes", n => n
                .Enrich("by-delegate", (Enrich<AlvoRecord> _) => { })
                .Enrich<ThrowingDisposalEnricher>("by-class")),
            services: services => services.AddScoped<ThrowingDisposalService>());
        var unit = new FakeFlowUnit();

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            var enrichment = world.Runner.BeginEnrichment("notes", DataOperation.List, ReadsWorld.Caller).ShouldNotBeNull();
            enrichment.Enrich(Row());

            var failure = await Should.ThrowAsync<HookFailedException>(async () => await enrichment.DisposeAsync());

            (failure.Reason, failure.Extensions.Single()).ShouldBe((HookFailedException.Exception, "by-class"));
            unit.Blamed.ShouldHaveSingleItem().ShouldBeSameAs(failure);
        }

        world.Logs.Entries.ShouldAllBe(entry => !entry.Message.Contains("disposal secret", StringComparison.Ordinal));
    }

    private static async Task<HookFailedException> FailureAsync(ReadsWorld world)
    {
        await using var enrichment = world.Runner.BeginEnrichment("notes", DataOperation.List, ReadsWorld.Caller).ShouldNotBeNull();

        return Should.Throw<HookFailedException>(() => enrichment.Enrich(Row()));
    }

    private static RowShaping Shaping(AlvoRecord? row = null) =>
        new(_notes, row ?? Row(), () => FormatCatalog.Build([_notes]));

    private static AlvoRecord Row() => new(new Dictionary<string, object?>
    {
        ["id"] = _id,
        ["tenant_id"] = ReadsWorld.Caller.Tenant!.Value.Value,
        ["created_at"] = DateTimeOffset.UnixEpoch,
        ["updated_at"] = DateTimeOffset.UnixEpoch,
        ["title"] = "a",
        ["owner_id"] = Guid.Empty,
        ["region"] = "north",
        ["salary"] = 1000L,
        ["display"] = null,
    });

    private static AlvoRecord Without(string field) =>
        new(Row().Values.Where(entry => entry.Key != field).ToDictionary(entry => entry.Key, entry => entry.Value));

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        internal int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;
            throw new InvalidOperationException("a delegate enricher takes no service");
        }
    }
}
