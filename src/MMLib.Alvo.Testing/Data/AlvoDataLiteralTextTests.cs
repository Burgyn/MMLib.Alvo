using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The literal text tests — <see cref="AlvoFilterOperator.StartsWith"/>, <see cref="AlvoFilterOperator.EndsWith"/>,
/// <see cref="AlvoFilterOperator.Contains"/> — answer the same on every engine: a <c>%</c>, <c>_</c>, <c>\</c> or <c>[</c> in the
/// value matches itself, case counts, and a missing value never matches.
/// </summary>
/// <remarks>
/// One shipped suite, inherited by every engine and by the in-memory reference, because the defect it guards is a
/// disagreement between them: a <c>LIKE</c> written without escaping answers <c>startsWith '50%'</c> with every row starting
/// with <c>50</c>, and the bracket class only T-SQL knows would make <c>contains '[x]'</c> a character class there.
/// </remarks>
public abstract class AlvoDataLiteralTextTests
{
    private const string Entity = "codes";

    private static readonly string[] _codes = ["50%_off", "50XYoff", "C:\\tmp", "C:Xtmp", "a_b", "axb", "[x]y", "xy", "Abc", "abc"];

    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>, seeded out of band
    /// with <paramref name="seed"/>'s rows — the seam <c>AlvoDataOrderingTests</c> defines.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>A percent sign in a prefix matches a percent sign, not "anything".</summary>
    [Fact]
    public async Task A_percent_in_a_starts_with_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.StartsWith, "50%"))).ShouldBe(["50%_off"], ignoreOrder: true);

    /// <summary>An underscore matches an underscore, not "any one character".</summary>
    [Fact]
    public async Task An_underscore_in_a_contains_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.Contains, "_"))).ShouldBe(["50%_off", "a_b"], ignoreOrder: true);

    /// <summary>A backslash — the escape character itself — matches a backslash.</summary>
    [Fact]
    public async Task A_backslash_in_an_ends_with_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.EndsWith, "\\tmp"))).ShouldBe(["C:\\tmp"], ignoreOrder: true);

    /// <summary>A bracket matches a bracket, on every engine — never a character class.</summary>
    [Fact]
    public async Task A_bracket_in_a_contains_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.Contains, "[x]"))).ShouldBe(["[x]y"], ignoreOrder: true);

    /// <summary>The tests are case-sensitive and ordinal, as the C# they translate from.</summary>
    [Fact]
    public async Task A_literal_text_test_is_case_sensitive()
    {
        (await CodesAsync(Code(AlvoFilterOperator.StartsWith, "abc"))).ShouldBe(["abc"], ignoreOrder: true);
        (await CodesAsync(Code(AlvoFilterOperator.EndsWith, "BC"))).ShouldBeEmpty();
    }

    /// <summary>An empty value matches every text, and a missing value never matches — SQL's three-valued answer.</summary>
    [Fact]
    public async Task An_empty_value_matches_every_text_and_never_a_missing_one()
        => (await CodesAsync(new AlvoComparison("label", AlvoFilterOperator.StartsWith, string.Empty))).ShouldBe(["xy", "abc"], ignoreOrder: true);

    /// <summary>A literal text test on a field that is not text is the port's malformed-filter refusal on every engine — never an empty page on one and an error on another.</summary>
    [Fact]
    public async Task A_literal_text_test_on_a_field_that_is_not_text_is_refused()
    {
        var data = await SeededAsync();

        (await Should.ThrowAsync<ArgumentException>(() => data.QueryAsync(
            new AlvoQuery { Entity = Entity, Filter = new AlvoComparison("rank", AlvoFilterOperator.StartsWith, "1") }, Caller, TestContext.Current.CancellationToken)))
            .Message.ShouldContain("'rank', which is not a text field");
    }

    /// <summary>A value that is not a string is the port's malformed-filter refusal on every engine.</summary>
    [Fact]
    public async Task A_value_that_is_not_a_string_is_refused()
    {
        var data = await SeededAsync();

        await Should.ThrowAsync<ArgumentException>(() => data.QueryAsync(
            new AlvoQuery { Entity = Entity, Filter = Code(AlvoFilterOperator.Contains, 5L) }, Caller, TestContext.Current.CancellationToken));
    }

    private static AlvoComparison Code(AlvoFilterOperator op, object? value) => new("code", op, value);

    private async Task<IReadOnlyList<string>> CodesAsync(AlvoFilter filter)
    {
        var data = await SeededAsync();
        var page = await data.QueryAsync(new AlvoQuery { Entity = Entity, Filter = filter }, Caller, TestContext.Current.CancellationToken);

        return [.. page.Items.Select(row => (string)row["code"]!)];
    }

    private Task<IAlvoData> SeededAsync()
    {
        var (descriptor, schema) = Fixture();
        var rows = _codes.Select(code => new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid(),
            ["code"] = code,
            ["label"] = code is "xy" or "abc" ? code : null,
        }));

        return CreateAsync(schema, descriptor, new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { [Entity] = [.. rows] });
    }

    private static (AlvoDescriptor Descriptor, SchemaModel Schema) Fixture()
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "literal-text-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Entity] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Global,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["code"] = new() { Type = DescField.String, Required = true, MaxLength = 40 },
                        ["label"] = new() { Type = DescField.String, MaxLength = 40 },
                        ["rank"] = new() { Type = DescField.Integer },
                    },
                    Rules = new AccessRules { List = "true", Get = "true" },
                },
            },
        };

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = Entity,
                Tenancy = TenancyMode.Global,
                Fields =
                [
                    new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "code", Type = SchemaField.String, Required = true, MaxLength = 40 },
                    new FieldSchema { Name = "label", Type = SchemaField.String, Nullable = true, MaxLength = 40 },
                    new FieldSchema { Name = "rank", Type = SchemaField.Integer, Nullable = true },
                ],
            },
        ]);

        return (descriptor, schema);
    }

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };
}
