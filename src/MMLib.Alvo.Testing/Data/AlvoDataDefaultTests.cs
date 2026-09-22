using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using System.Text.Json;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// What a literal <c>field.default</c> does over a real engine (#113's literal half), asked of both shipped
/// drivers so §0 principle 3's "identical behaviour" is measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>It needs a real database, which is why it is here rather than in the adversarial suite.</b> Every fact
/// is about what a row <em>holds</em> after a write the engine performed, and two of them are about how EF
/// composes the statement. An in-memory reference implementation would have to grow a second implementation
/// of the feature to answer them at all, which would then be the thing under test.
/// </para>
/// <para>
/// <b>The third fact is the one that earns the suite.</b> EF reads a property's CLR default as "not set" on a
/// value-generated column, so a build that annotated the runtime model would replace a caller's explicit
/// <see langword="false"/> with the declared <see langword="true"/> — silently, and only for the value that
/// happens to be the CLR default. That is the wrong-stored-value failure the old refusal existed to prevent,
/// and nothing but a write that sends it would notice.
/// </para>
/// </remarks>
public abstract class AlvoDataDefaultTests
{
    private const string Tasks = "tasks";
    private const string Done = "done";
    private const string Priority = "priority";
    private const string Title = "title";

    /// <summary>Builds a fresh port over the fixture's schema and descriptor.</summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A create that omits the field stores the declared default.</summary>
    [Fact]
    public async Task A_create_that_omits_the_field_stores_the_default()
    {
        var data = await CreateAsync(Schema, Descriptor);

        var stored = await WriteAsync(data, Payload((Title, "write it down")));

        stored[Done].ShouldBe(false);
        stored[Priority].ShouldBe("normal");
    }

    /// <summary>A create that sends a value stores that value, not the default.</summary>
    [Fact]
    public async Task A_create_that_sends_a_value_stores_what_it_sent()
    {
        var data = await CreateAsync(Schema, Descriptor);

        var stored = await WriteAsync(
            data, Payload((Title, "urgent"), (Priority, "high"), (Done, true)));

        stored[Priority].ShouldBe("high");
        stored[Done].ShouldBe(true);
    }

    /// <summary>
    /// A value that <em>is</em> the CLR default of its type is still the caller's value.
    /// </summary>
    /// <remarks>
    /// The trap this suite exists for. With the default declared as <see langword="true"/> and the caller
    /// sending <see langword="false"/>, an implementation that lets EF treat "CLR default" as "not set" stores
    /// <see langword="true"/> — the opposite of what was sent, for one value only.
    /// </remarks>
    [Fact]
    public async Task A_caller_who_sends_the_clr_default_value_stores_that_value()
    {
        var data = await CreateAsync(SchemaDefaultingToTrue, DescriptorDefaultingToTrue);

        var stored = await WriteAsync(data, Payload((Title, "not done"), (Done, false)));

        stored[Done].ShouldBe(false);
    }

    /// <summary>
    /// A replacement takes the defaults a create takes, so the same <c>PUT</c> twice produces the same row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression this fact exists for. Tying the fill to the audit stamp's <c>isUpdate</c> gave the
    /// create branch its defaults and left the replace branch without them, so a <c>PUT</c> against a fresh id
    /// stored the default and the identical <c>PUT</c> that followed stored <see langword="null"/> — two
    /// identical replacements producing two different rows, which is the property the whole-row guard exists
    /// to keep.
    /// </para>
    /// <para>
    /// Asked of a replacement over a row that <em>already holds</em> a non-default value, because that is the
    /// direction the bug survives in: replacing an untouched row would answer the same either way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_replacement_takes_the_same_defaults_a_create_does()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var created = await WriteAsync(data, Payload((Title, "first"), (Priority, "high")));

        var replaced = await data.ReplaceAsync(
            Tasks, (Guid)created["id"]!, Payload((Title, "second")), _caller, cancellationToken: Ct);

        replaced.Row[Priority].ShouldBe("normal", "a replacement composes a whole row, defaults included");
        replaced.Row[Done].ShouldBe(false);
    }

    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private static Task<AlvoRecord> WriteAsync(IAlvoData data, Dictionary<string, object?> values) =>
        data.CreateAsync(Tasks, values, _caller, cancellationToken: Ct);

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] values)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in values)
        {
            payload[field] = value;
        }

        return payload;
    }

    private static AlvoDescriptor Descriptor => DescriptorWith(doneDefault: false);

    private static AlvoDescriptor DescriptorDefaultingToTrue => DescriptorWith(doneDefault: true);

    private static SchemaModel Schema => new([TasksEntity(doneDefault: false)]);

    private static SchemaModel SchemaDefaultingToTrue => new([TasksEntity(doneDefault: true)]);

    private static AlvoDescriptor DescriptorWith(bool doneDefault) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "default-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Tasks] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [Title] = new() { Type = DescField.String, MaxLength = 120 },
                    [Done] = new() { Type = DescField.Boolean, Default = ValueOrExpr.FromLiteral(Literal(doneDefault)) },
                    [Priority] = new()
                    {
                        Type = DescField.String,
                        MaxLength = 20,
                        Default = ValueOrExpr.FromLiteral(Literal("normal")),
                    },
                },
                Rules = AllowAll,
            },
        },
    };

    private static EntitySchema TasksEntity(bool doneDefault) => new()
    {
        Name = Tasks,
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            new FieldSchema { Name = Title, Type = SchemaField.String, MaxLength = 120, Nullable = true },
            new FieldSchema { Name = Done, Type = SchemaField.Boolean, Nullable = true, Default = Literal(doneDefault) },
            new FieldSchema
            {
                Name = Priority,
                Type = SchemaField.String,
                MaxLength = 20,
                Nullable = true,
                Default = Literal("normal"),
            },
        ],
    };

    private static JsonElement Literal(bool value) => Parse(value ? "true" : "false");

    private static JsonElement Literal(string value) => Parse($"\"{value}\"");

    /// <summary>
    /// A literal that outlives the document it was parsed from.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonDocument"/> owns pooled memory that a <see cref="JsonElement"/> points into, so a clone
    /// is what makes the value safe to hold — the same reason the mapper clones what it stores.
    /// </remarks>
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };
}
