using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using System.Reflection;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>Emitted wrappers, compiled and loaded, against the real core: typed reads, <c>As&lt;T&gt;()</c>, writes and patches.</summary>
public sealed class GeneratedWrapperTests
{
    private const string AllKinds = """
        { "apiVersion": "alvo.dev/v1", "name": "help-desk", "entities": { "samples": { "fields": {
            "name": { "type": "string", "required": true }, "body": { "type": "text" }, "count": { "type": "integer", "required": true },
            "price": { "type": "decimal", "precision": 9, "scale": 2 }, "paid": { "type": "boolean", "required": true },
            "due_on": { "type": "date" }, "due_at": { "type": "datetime" }, "owner": { "type": "uuid" },
            "parent": { "type": "ref", "entity": "samples" }, "level": { "type": "enum", "values": ["low", "in-review"], "required": true },
            "label": { "type": "string", "virtual": true } } } } }
        """;

    private const string AllKindsProbe = """
        using MMLib.Alvo.Data;
        internal static class Probe
        {
            public static object?[] Read(AlvoRecord record)
            {
                var sample = new HelpDesk.Sample(record);
                return [sample.Id, sample.Name, sample.Body, sample.Count, sample.Price, sample.Paid, sample.DueOn, sample.DueAt, sample.Owner, sample.Parent, sample.Level, sample.Label];
            }
        }
        """;

    private const string TicketProbe = """
        using MMLib.Alvo.Data;
        internal static class Probe
        {
            public static object UnsavedId() => HelpDesk.Ticket.Create("printer jam").Id;
            public static object? Title(AlvoRecord record) => record.As<HelpDesk.Ticket>().Title;
            public static object Wrap(AlvoRecord record) => record.As<HelpDesk.Ticket>();
            public static object Patch() => HelpDesk.Ticket.Patch().SetPriority(HelpDesk.TicketPriority.High).SetTitle("renamed").Values;
            public static object PatchThroughTheContract()
            {
                IAlvoPatch<HelpDesk.Ticket> patch = HelpDesk.Ticket.Patch();
                return patch.Set(x => x.Status, HelpDesk.TicketStatus.Closed);
            }
        }
        """;

    private static readonly Guid _id = Guid.Parse("0b6f6a7e-4a8e-4bdb-9f3e-0f2b9b6e1c11");

    [Fact]
    public void A_wrapper_reads_every_type_through_Get()
    {
        var assembly = GeneratorHarness.Load(AllKinds, AllKindsProbe);
        var owner = Guid.NewGuid();
        var record = Record(
            ("id", _id.ToString()), ("name", "n"), ("body", null), ("count", 3L), ("price", 12L), ("paid", true), ("due_on", "2026-11-03"),
            ("due_at", "2026-11-02T08:30:00Z"), ("owner", owner), ("parent", null), ("level", "in-review"));

        var values = (object?[])GeneratorHarness.Run(assembly, "Read", record)!;

        values[..10].ShouldBe([
            _id, "n", null, 3L, 12m, true, new DateOnly(2026, 11, 3), new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero), owner, null]);
        values[10]!.ToString().ShouldBe("InReview");
        values[11].ShouldBeNull("a virtual field the record does not carry reads null");
    }

    [Fact]
    public void Reading_the_id_of_an_unsaved_wrapper_names_the_missing_field()
    {
        var assembly = GeneratorHarness.Load(Descriptors.HelpDesk, TicketProbe);

        var failure = Should.Throw<InvalidOperationException>(() => GeneratorHarness.Run(assembly, "UnsavedId"));

        failure.Message.ShouldContain("'id'");
        failure.Message.ShouldContain("not stored yet");
    }

    [Fact]
    public void As_wraps_a_record_into_a_generated_class_and_keeps_unknown_fields()
    {
        var assembly = GeneratorHarness.Load(Descriptors.HelpDesk, TicketProbe);
        var record = Record(
            ("id", _id), ("title", "jam"), ("body", null), ("priority", "high"), ("status", "open"), ("estimate_hours", null),
            ("hourly_rate", null), ("estimate_cost", null), ("created_at", DateTime.UtcNow), ("created_by", null),
            ("updated_at", DateTime.UtcNow), ("updated_by", null), ("legacy_code", "L-7"));

        GeneratorHarness.Run(assembly, "Title", record).ShouldBe("jam");
        var wrapper = (IAlvoEntity)GeneratorHarness.Run(assembly, "Wrap", record)!;
        wrapper.Record["legacy_code"].ShouldBe("L-7");
        var sent = WriterValues(wrapper);
        sent["legacy_code"].ShouldBe("L-7");
        sent.ShouldNotContainKey("id");
    }

    [Fact]
    public void A_generated_patch_converts_an_enum_to_its_declared_value()
    {
        var assembly = GeneratorHarness.Load(Descriptors.HelpDesk, TicketProbe);

        var values = (IReadOnlyDictionary<string, object?>)GeneratorHarness.Run(assembly, "Patch")!;

        values.ShouldBe(new Dictionary<string, object?> { ["priority"] = "high", ["title"] = "renamed" }, ignoreOrder: true);
    }

    [Fact]
    public void The_patch_s_own_Set_answers_the_generated_patch()
    {
        var assembly = GeneratorHarness.Load(Descriptors.HelpDesk, TicketProbe);

        var patch = GeneratorHarness.Run(assembly, "PatchThroughTheContract")!;

        patch.GetType().FullName.ShouldBe("HelpDesk.TicketPatch");
        var values = (IReadOnlyDictionary<string, object?>)patch.GetType().GetProperty("Values")!.GetValue(patch)!;
        values["status"].ShouldBe("closed");
    }

    private static Dictionary<string, object?> WriterValues(IAlvoEntity wrapper) =>
        (Dictionary<string, object?>)typeof(RecordWriter).GetMethod(nameof(RecordWriter.Values), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(wrapper.GetType())
            .Invoke(null, [wrapper, false, null])!;

    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal));
}
