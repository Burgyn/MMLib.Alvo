using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// What a <c>decimal</c> <c>computed</c> field actually <b>stores</b> on SQLite, pinned because it is the one
/// place this driver's answer differs from PostgreSQL's for the same descriptor — and because the difference is
/// invisible to every value-reading fact that happens to pick a number a double can hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state this pins is a deviation, not the intended end state.</b> An ordinary <c>decimal</c> column is
/// <c>TEXT</c> on this driver and holds EF's exact formatting of the value. A <em>computed</em> one carries no
/// store type at all, because EF Core's SQLite migrations generator emits a computed column as
/// <c>"col" AS (&lt;expr&gt;) STORED</c> and drops the column type unconditionally — measured through the
/// product's own snapshot suite by configuring <c>HasColumnType</c> on the property, with both the real store
/// type (<c>TEXT</c>) and a deliberately bogus one: the emitted DDL was byte-identical either way. So the
/// dialect's <c>GeneratedColumnDefinition</c> — whose reference spelling <em>does</em> name the type — cannot
/// close this, and neither can the model builder.
/// </para>
/// <para>
/// <b>And naming the type would not change the value anyway.</b> SQLite has no decimal arithmetic: the
/// expression is evaluated as IEEE-754 double whatever affinity the column declares. Measured on the bundled
/// provider, <c>'0.1' * 3</c> stores <c>0.30000000000000004</c> in an untyped column, in a <c>TEXT</c> one and
/// in a <c>REAL</c> one alike, and <c>SUM</c> over each of the three answers identically — a <c>TEXT</c>
/// affinity merely stores the double's own text. The residual difference from PostgreSQL, where the same field
/// is <c>numeric(18,2)</c> and answers <c>0.30</c>, is therefore SQLite's arithmetic and the missing rounding to
/// the field's declared scale, not the column's affinity. Closing it means the driver rounding a computed
/// decimal expression to its scale, which needs a seam the ports do not have yet; it is tracked as its own
/// issue (#162) and recorded in the design's open questions.
/// </para>
/// <para>
/// So this fact exists to make the deviation <b>checkable</b> rather than described: if a provider bump starts
/// emitting the store type, or starts rounding, this goes red and whoever changed it reads this remark instead
/// of discovering the divergence from a customer's invoice total.
/// </para>
/// </remarks>
public sealed class SqliteComputedDecimalStorageTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_computed_decimal_column_carries_no_store_type_while_an_ordinary_one_is_text()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        await CreateLineAsync(host, unitPrice: 0.1m, amount: 3);

        var storage = await StorageClassesAsync(host);

        storage.UnitPrice.ShouldBe("text", "an ordinary decimal column is TEXT on this driver, and exact");
        storage.LineTotal.ShouldBe(
            "real",
            "the generated column has no declared type, so it holds whatever the expression evaluated to");
    }

    [Fact]
    public async Task A_computed_decimal_is_the_engines_double_arithmetic_not_the_declared_scale()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        var stored = await CreateLineAsync(host, unitPrice: 0.1m, amount: 3);

        stored[LineTotal].ShouldBe(
            0.30000000000000004m,
            "SQLite evaluates the expression as a double and rounds to no scale; PostgreSQL answers 0.30 for "
            + "the same descriptor, and that difference is #162, the open item this fact pins");
    }

    private static async Task<AlvoRecord> CreateLineAsync(AlvoDataHost host, decimal unitPrice, int amount) =>
        await host.Data.CreateAsync(
            Lines,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unit_price"] = unitPrice,
                ["amount"] = amount,
            },
            Caller,
            cancellationToken: Ct);

    /// <summary>
    /// The engine's own storage class for the two columns, read on a connection of its own — <c>typeof()</c> is
    /// the only way to ask SQLite what a value actually is, and it cannot be asked through the data port.
    /// </summary>
    private static async Task<(string UnitPrice, string LineTotal)> StorageClassesAsync(AlvoDataHost host)
    {
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(Ct);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT typeof(\"unit_price\"), typeof(\"{LineTotal}\") FROM \"{Lines}\"";
        using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        return (reader.GetString(0), reader.GetString(1));
    }

    private const string Lines = "lines";

    private const string LineTotal = "line_total";

    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private static AlvoContext Caller => _caller;

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "computed-decimal-storage",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Lines] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["unit_price"] = new() { Type = DescField.Decimal, Required = true },
                    ["amount"] = new() { Type = DescField.Integer, Required = true },
                    [LineTotal] = new() { Type = DescField.Decimal, Computed = "unit_price * amount" },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
            },
        },
    };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Lines,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                Money("unit_price") with { Required = true, Nullable = false },
                new FieldSchema { Name = "amount", Type = SchemaField.Integer, Required = true },
                Money(LineTotal) with { ComputedExpression = "unit_price * amount" },
            ],
        },
    ]);

    private static FieldSchema Money(string name) => new()
    {
        Name = name,
        Type = SchemaField.Decimal,
        Precision = 18,
        Scale = 2,
        Nullable = true,
    };

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
