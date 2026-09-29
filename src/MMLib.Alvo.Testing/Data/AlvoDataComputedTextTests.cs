using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A computed field that joins text — <c>full_name = first_name + ' ' + last_name</c>, the maintainer's own first
/// request (assistant-reliability design, ruling 2) — over a real engine, asked of both shipped drivers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own suite, for <see cref="AlvoDataComputedRollupTests"/>' reason:</b> a generated column is maintained by the
/// engine, so every fact needs one. The ladder suite's fixture is an invoice; this one is the customer the request was
/// about, so a fact here reads like the request.
/// </para>
/// <para>
/// <b>The adversarial fact is the security one.</b> A text constant is written inline into DDL — there is no bind
/// parameter there — so the suite writes a constant built to break out of a literal on either engine (a quote, a
/// backslash before a quote, a comment opener, a terminator, a dollar quote, an escape-string prefix) and asserts it
/// lands in the column as data, byte for byte, with the table it names intact.
/// </para>
/// </remarks>
public abstract class AlvoDataComputedTextTests
{
    /// <summary>
    /// Text built to end a literal early on either shipped engine: a standard literal's closing quote, PostgreSQL's
    /// backslash escape (live when <c>standard_conforming_strings</c> is off), a statement terminator, a comment
    /// opener, a dollar quote and an escape-string prefix — plus text outside ASCII, so an encoding slip shows too.
    /// </summary>
    private const string Hostile = " \\'); DROP TABLE customers; -- \" ; E'x' $$ /* 🚲 ž */ ";

    private const string Customers = "customers";

    private const string FullName = "full_name";

    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    /// <summary>Builds a fresh <see cref="IAlvoData"/> over the schema and descriptor, as the sibling suites' seam does.</summary>
    /// <param name="schema">The schema the descriptor maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>
    /// Executes one statement outside the data port against the store the most recent <see cref="CreateAsync"/> stood
    /// up, returning the engine's failure or <see langword="null"/> — for the one fact that must ask the engine.
    /// </summary>
    /// <param name="sql">The statement. Identifiers are double-quoted, which both engines accept.</param>
    protected abstract Task<Exception?> ExecuteOutOfBandAsync(string sql);

    /// <summary>Plans and applies <paramref name="current"/> → <paramref name="desired"/>, re-priming the port.</summary>
    /// <param name="current">The schema the store holds.</param>
    /// <param name="desired">The schema to migrate it to.</param>
    protected abstract Task<MigrationResult> MigrateAsync(SchemaModel current, SchemaModel desired);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The request itself: the engine joins the row it stored, diacritics and all.</summary>
    [Fact]
    public async Task A_computed_text_field_is_the_join_of_the_row_the_write_stored()
    {
        var data = await CreateAsync(Schema(Joined), Descriptor(Joined));

        var stored = await CreateCustomerAsync(data, "Jana", "Nováková");

        stored[FullName].ShouldBe("Jana Nováková");
    }

    /// <summary>An update to a part moves the joined value — the engine tracks it, nothing in Alvo recomputes it.</summary>
    [Fact]
    public async Task An_update_to_a_part_moves_the_joined_value()
    {
        var data = await CreateAsync(Schema(Joined), Descriptor(Joined));
        var stored = await CreateCustomerAsync(data, "Jana", "Nováková");

        var updated = await data.UpdateAsync(
            Customers,
            (Guid)stored["id"]!,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["last_name"] = "Kováčová" },
            _caller,
            cancellationToken: Ct);

        updated[FullName].ShouldBe("Jana Kováčová");
    }

    /// <summary>
    /// A constant built to break out of its literal lands as data: the value is the three parts byte for byte, and the
    /// table its payload names still answers.
    /// </summary>
    [Fact]
    public async Task A_constant_built_to_break_out_of_its_literal_lands_as_data()
    {
        var computed = $"first_name + '{EscapeForCel(Hostile)}' + last_name";
        var data = await CreateAsync(Schema(computed), Descriptor(computed));

        var stored = await CreateCustomerAsync(data, "Jana", "Nováková");

        stored[FullName].ShouldBe("Jana" + Hostile + "Nováková");
        (await data.QueryAsync(new AlvoQuery { Entity = Customers }, _caller, Ct)).Items.Count
            .ShouldBe(1, "the table the payload names is still there, holding the row");
    }

    /// <summary>
    /// The field added to an entity that already holds rows — the operator's case: on SQLite that is the table rebuild
    /// (a bare <c>ADD COLUMN … STORED</c> is refused on a populated table), on PostgreSQL an in-place add that backfills.
    /// The rows are written <b>first</b>; asserted by value and by the engine refusing a write to the column.
    /// </summary>
    [Fact]
    public async Task A_computed_text_field_added_to_a_populated_entity_is_computed_for_every_row()
    {
        var data = await CreateAsync(Schema(computed: null), Descriptor(computed: null));
        await CreateCustomerAsync(data, "Jana", "Nováková");                  // FIRST. Not a detail.
        await CreateCustomerAsync(data, "Ján", "Kováč");

        var migration = await MigrateAsync(Schema(computed: null), Schema(Joined));

        migration.Applied.ShouldBeTrue();
        migration.Plan.HasDestructiveChanges.ShouldBeFalse();
        var names = (await data.QueryAsync(new AlvoQuery { Entity = Customers }, _caller, Ct)).Items
            .Select(customer => customer[FullName]);
        names.ShouldBe(["Jana Nováková", "Ján Kováč"], ignoreOrder: true);
        (await ExecuteOutOfBandAsync($"UPDATE \"{Customers}\" SET \"{FullName}\" = 'x'")).ShouldNotBeNull(
            "and it is a GENERATED column afterwards, not an ordinary one the rebuild filled in");
    }

    private const string Joined = "first_name + ' ' + last_name";

    private static string EscapeForCel(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static Task<AlvoRecord> CreateCustomerAsync(IAlvoData data, string firstName, string lastName) =>
        data.CreateAsync(
            Customers,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["first_name"] = firstName, ["last_name"] = lastName },
            _caller,
            cancellationToken: Ct);

    /// <summary>The customer's descriptor, with <c>full_name</c> computed from <paramref name="computed"/> or absent.</summary>
    private static AlvoDescriptor Descriptor(string? computed)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["first_name"] = new() { Type = DescField.String, Required = true, MaxLength = 60 },
            ["last_name"] = new() { Type = DescField.String, Required = true, MaxLength = 60 },
        };
        if (computed is not null)
        {
            fields[FullName] = new() { Type = DescField.String, Computed = computed };
        }

        return new()
        {
            ApiVersion = "alvo.dev/v1",
            Name = "computed-text-suite",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Customers] = new()
                {
                    Tenancy = EntityTenancy.Global,
                    Fields = fields,
                    Rules = new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
                },
            },
        };
    }

    /// <summary>The applied schema <see cref="Descriptor"/> maps to, paired by hand as the sibling suites pair theirs.</summary>
    private static SchemaModel Schema(string? computed) => new([
        new EntitySchema
        {
            Name = Customers,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "first_name", Type = SchemaField.String, MaxLength = 60, Required = true },
                new FieldSchema { Name = "last_name", Type = SchemaField.String, MaxLength = 60, Required = true },
                .. computed is null
                    ? Array.Empty<FieldSchema>()
                    : [new FieldSchema { Name = FullName, Type = SchemaField.String, Nullable = true, ComputedExpression = computed }],
            ],
        },
    ]);
}
