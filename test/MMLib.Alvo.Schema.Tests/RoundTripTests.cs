using CsCheck;
using MMLib.Alvo.Descriptor;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

public class RoundTripTests
{
    public static IEnumerable<object[]> Examples() =>
        SchemaPaths.Examples().Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(Examples))]
    public void Canonicalize_is_idempotent_and_member_order_insensitive(string path)
    {
        string original = File.ReadAllText(path);
        string once = Canonicalizer.Canonicalize(original);

        Canonicalizer.Canonicalize(once)
            .ShouldBe(once, $"canonicalization must be idempotent for {Path.GetFileName(path)}");
        Canonicalizer.Canonicalize(ReverseTopLevelMembers(original))
            .ShouldBe(once, $"canonicalization must ignore member order for {Path.GetFileName(path)}");
    }

    [Fact]
    public void A_mutated_valid_example_fails_validation()
    {
        var schema = SchemaValidator.Load();
        string crm = File.ReadAllText(SchemaPaths.Examples().First(path => path.Contains("complex-crm")));

        // A typo'd keyword must never slip through: an unknown, non-`x-` key is
        // rejected for any name, not just the ones a fixed example happens to try.
        Gen.String[1, 12].Where(s => s.Length > 0 && s.All(char.IsLetter)).Sample(key =>
        {
            using JsonDocument document = JsonDocument.Parse(crm);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WritePropertyName("zzz" + key);
            writer.WriteStringValue("boom");
            foreach (JsonProperty member in document.RootElement.EnumerateObject())
            {
                member.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.Flush();

            string mutated = Encoding.UTF8.GetString(stream.ToArray());
            return SchemaValidator.Failures(schema, mutated).Count > 0;
        });
    }

    [Fact]
    public void A_generated_sequence_validates_and_survives_parse_and_serialize()
    {
        var schema = SchemaValidator.Load();

        GeneratedSequenceDescriptor().Sample(descriptor =>
        {
            SchemaValidator.Failures(schema, descriptor).ShouldBeEmpty(descriptor);
            var parsed = AlvoDescriptor.Parse(descriptor);
            parsed.Entities["orders"].Fields["number"].Sequence.ShouldNotBeNull(descriptor);
            parsed.Entities["orders"].Fields["number"].Extensions.ShouldBeNull(descriptor);
            Canonicalizer.Canonicalize(AlvoDescriptor.Serialize(AlvoDescriptor.Parse(descriptor)))
                .ShouldBe(Canonicalizer.Canonicalize(descriptor), descriptor);
        });
    }

    private static Gen<string> GeneratedSequenceDescriptor() =>
        Gen.Select(
            Gen.Bool,
            Gen.OneOfConst<string?>(null, "branch"),
            Gen.OneOfConst<string?>(null, "year", "month", "day"),
            Gen.OneOfConst<long?>(null, 0, 1, 1000, 999_999),
            Gen.OneOfConst("{n}", "SO-{yyyy}-{n:4}", "{yy}{MM}{dd}/{n:18}", "INV {n:1}"),
            SequenceDescriptor);

    private static string SequenceDescriptor(bool asText, string? per, string? period, long? start, string format)
    {
        var sequence = new Dictionary<string, object?>
        {
            ["per"] = per,
            ["period"] = period,
            ["start"] = start,
            ["format"] = asText ? format : null,
        }.Where(member => member.Value is not null).ToDictionary(member => member.Key, member => member.Value);
        var fields = new Dictionary<string, object>
        {
            ["branch"] = new { type = "string", required = true },
            ["number"] = new { type = asText ? "string" : "integer", sequence },
        };

        return JsonSerializer.Serialize(new
        {
            apiVersion = "alvo.dev/v1",
            name = "sequences",
            entities = new { orders = new { fields } },
        });
    }

    [Fact]
    public void A_generated_index_validates_and_survives_parse_and_serialize()
    {
        var schema = SchemaValidator.Load();

        GeneratedIndexDescriptor().Sample(descriptor =>
        {
            SchemaValidator.Failures(schema, descriptor).ShouldBeEmpty(descriptor);
            var index = AlvoDescriptor.Parse(descriptor).Entities["bookings"].Indexes.ShouldNotBeNull(descriptor).ShouldHaveSingleItem();
            index.Fields.ShouldNotBeEmpty(descriptor);
            (index.Exclude is null).ShouldBe(!descriptor.Contains("\"exclude\"", StringComparison.Ordinal), descriptor);
            Canonicalizer.Canonicalize(AlvoDescriptor.Serialize(AlvoDescriptor.Parse(descriptor)))
                .ShouldBe(Canonicalizer.Canonicalize(descriptor), descriptor);
        });
    }

    private static readonly string[] _indexedFields = ["room"];

    private static Gen<string> GeneratedIndexDescriptor() =>
        Gen.Select(
            Gen.OneOfConst<bool?>(null, false, true),
            Gen.OneOfConst<string?>(null, "active", "status != 'cancelled'", "room == 'a' && active"),
            Gen.Bool,
            IndexDescriptor);

    private static string IndexDescriptor(bool? unique, string? where, bool excluding)
    {
        var index = new Dictionary<string, object?>
        {
            ["fields"] = _indexedFields,
            ["unique"] = unique,
            ["where"] = where,
            ["exclude"] = excluding ? new { from = "starts_at", to = "ends_at" } : null,
        }.Where(member => member.Value is not null).ToDictionary(member => member.Key, member => member.Value);
        var fields = new Dictionary<string, object>
        {
            ["room"] = new { type = "string", required = true },
            ["active"] = new { type = "boolean", required = true },
            ["status"] = new { type = "string" },
            ["starts_at"] = new { type = "datetime", required = true },
            ["ends_at"] = new { type = "datetime", required = true },
        };

        return JsonSerializer.Serialize(new
        {
            apiVersion = "alvo.dev/v1",
            name = "indexes",
            entities = new { bookings = new { fields, indexes = new[] { index } } },
        });
    }

    [Fact]
    public void A_generated_state_machine_and_timers_validate_and_survive_parse_and_serialize()
    {
        var schema = SchemaValidator.Load();

        GeneratedLifecycleDescriptor().Sample(descriptor =>
        {
            SchemaValidator.Failures(schema, descriptor).ShouldBeEmpty(descriptor);
            var bookings = AlvoDescriptor.Parse(descriptor).Entities["bookings"];
            bookings.Extensions.ShouldBeNull(descriptor);
            bookings.StateMachine.ShouldNotBeNull(descriptor).Transitions["reopen"].From.ShouldBe(_anySource, descriptor);
            bookings.Timers.ShouldNotBeNull(descriptor)["reminder"].At.ShouldBe("starts_at - duration('24h')", descriptor);
            Canonicalizer.Canonicalize(AlvoDescriptor.Serialize(AlvoDescriptor.Parse(descriptor)))
                .ShouldBe(Canonicalizer.Canonicalize(descriptor), descriptor);
        });
    }

    private static readonly string[] _draftSource = ["draft"];
    private static readonly string[] _anySource = ["*"];
    private static readonly string[] _bookingStates = ["draft", "confirmed"];

    private static Gen<string> GeneratedLifecycleDescriptor() =>
        Gen.Select(
            Gen.OneOfConst<string?>(null, "draft"),
            Gen.OneOfConst<string[]?>(null, ["manager"], ["manager", "admin"]),
            Gen.OneOfConst<string?>(null, "new.starts_at > now()"),
            Gen.OneOfConst<string?>(null, "status == 'confirmed'"),
            Gen.OneOfConst<string?>(null, "bookings.reminder_due"),
            LifecycleDescriptor);

    private static string LifecycleDescriptor(string? initial, string[]? roles, string? when, string? timerWhen, string? timerEvent)
    {
        var confirm = Present(new Dictionary<string, object?>
        {
            ["from"] = _draftSource,
            ["to"] = "confirmed",
            ["roles"] = roles,
            ["when"] = when,
        });
        var stateMachine = Present(new Dictionary<string, object?>
        {
            ["field"] = "status",
            ["initial"] = initial,
            ["transitions"] = new Dictionary<string, object>
            {
                ["confirm"] = confirm,
                ["reopen"] = new { from = _anySource, to = "draft", description = "Back to the start." },
            },
        });
        var reminder = Present(new Dictionary<string, object?>
        {
            ["at"] = "starts_at - duration('24h')",
            ["when"] = timerWhen,
            ["event"] = timerEvent,
        });
        var fields = new Dictionary<string, object>
        {
            ["status"] = new { type = "enum", values = _bookingStates, required = true },
            ["starts_at"] = new { type = "datetime" },
        };

        return JsonSerializer.Serialize(new
        {
            apiVersion = "alvo.dev/v1",
            name = "lifecycles",
            entities = new { bookings = new { fields, stateMachine, timers = new Dictionary<string, object> { ["reminder"] = reminder } } },
        });
    }

    private static Dictionary<string, object?> Present(Dictionary<string, object?> members) =>
        members.Where(member => member.Value is not null).ToDictionary(member => member.Key, member => member.Value);

    private static string ReverseTopLevelMembers(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (JsonProperty member in document.RootElement.EnumerateObject().Reverse())
        {
            member.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
