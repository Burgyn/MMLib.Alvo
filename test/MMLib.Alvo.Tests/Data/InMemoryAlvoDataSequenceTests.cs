using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>The in-memory store assigns sequence numbers (HX-12 D14), so a host's unit test reads what its creates would get.</summary>
public sealed class InMemoryAlvoDataSequenceTests
{
    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public async Task Creates_number_per_value_and_period_from_start()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero));
        var data = Store(clock);

        (await CreatedAsync(data, "north")).ShouldBe((10L, "SO-2026-0001"));
        (await CreatedAsync(data, "south")).ShouldBe((10L, "SO-2026-0002"));
        (await CreatedAsync(data, "north")).ShouldBe((11L, "SO-2026-0003"));
        clock.SetUtcNow(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        (await CreatedAsync(data, "north")).ShouldBe((12L, "SO-2027-0001"));
    }

    [Fact]
    public async Task A_batch_numbers_its_rows_in_order()
    {
        var data = Store(new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)));

        var batch = await data.CreateManyAsync("orders", [Row("north"), Row("north")], _caller, cancellationToken: TestContext.Current.CancellationToken);

        batch.Rows.Select(row => row["number"]).ShouldBe([10L, 11L]);
    }

    private static async Task<(long Number, string Code)> CreatedAsync(InMemoryAlvoData data, string branch)
    {
        var row = await data.CreateAsync("orders", Row(branch), _caller, cancellationToken: TestContext.Current.CancellationToken);
        return ((long)row["number"]!, (string)row["code"]!);
    }

    private static Dictionary<string, object?> Row(string branch) => new(StringComparer.Ordinal) { ["branch"] = branch };

    private static InMemoryAlvoData Store(TimeProvider clock)
    {
        var catalog = PolicyCatalog.Build(Descriptor, Schema, MMLib.Alvo.Tests.Expressions.CelFixtures.Compiler);
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(Descriptor.Name, catalog);
        return new InMemoryAlvoData(new PolicyEngine(provider), new PredicateEvaluator(), Schema, clock);
    }

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = "orders",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = MMLib.Alvo.Schema.FieldType.Uuid, Required = true },
                new FieldSchema { Name = "branch", Type = MMLib.Alvo.Schema.FieldType.String, Required = true },
                new FieldSchema
                {
                    Name = "number",
                    Type = MMLib.Alvo.Schema.FieldType.Integer,
                    Required = true,
                    Sequence = new SequenceSchema { Per = "branch", Start = 10 },
                },
                new FieldSchema
                {
                    Name = "code",
                    Type = MMLib.Alvo.Schema.FieldType.String,
                    Required = true,
                    Sequence = new SequenceSchema { Period = SequenceResetPeriod.Year, Format = "SO-{yyyy}-{n:4}" },
                },
            ],
        },
    ]);

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "in-memory-sequences",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["orders"] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["branch"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.String, Required = true },
                    ["number"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Integer, Required = true, Sequence = new FieldSequence { Per = "branch", Start = 10 } },
                    ["code"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.String, Required = true, Sequence = new FieldSequence { Period = SequencePeriod.Year, Format = "SO-{yyyy}-{n:4}" } },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };
}
