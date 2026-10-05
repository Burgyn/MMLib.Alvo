using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;
using System.Text.Json;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// A before-hook's <c>mutate</c> result honours the target field's declared facets — <c>maxLength</c>, enum
/// membership, <c>format</c>, decimal precision and scale, <c>required</c> — measured by the same checks a
/// caller's payload is (Ruling V, #308).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the runner, and not the driver.</b> Every driver applies the patch <see cref="IBeforeHookRunner.Run"/>
/// answers, so refusing it here is what makes SQLite (which enforces no length) and PostgreSQL (whose
/// <c>varchar(n)</c> refused with an anonymous 500) answer the same.
/// </para>
/// <para>
/// <b>The refusal is a hook refusal</b> — <see cref="AlvoAuthorizationException"/>, the family a <c>reject</c> uses —
/// naming the hook's pointer, the field and the facet, never the value: the value may be the caller's own text
/// grown by <c>replace</c>, or whatever a host function returned.
/// </para>
/// </remarks>
public sealed class BeforeHookFacetTests
{
    private const string Contacts = "contacts";
    private const string CreatePointer = "/entities/contacts/hooks/beforeCreate/0";
    private const string Secret = "SECRET-VALUE-abcdefgh";

    public static TheoryData<string, string, object?, string> Refused => new()
    {
        { "code", "new.note", Secret, "max-length" },
        { "code", "replace(new.note, 'a', 'aaaaaaaaaa')", "aa", "max-length" },
        { "code", "pad(new.note)", "x", "max-length" },
        { "stage", "new.note", "superadmin", "enum-value" },
        { "email", "new.note", "not-an-address", "format" },
        { "title", "new.note", null, "required" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_mutate_result_outside_the_target_fields_facets_refuses_the_write(
        string field, string expression, object? note, string facet)
    {
        var refusal = Should.Throw<AlvoAuthorizationException>(
            () => Run(BeforeCreate(Mutate(field, Cel(expression))), Row(("note", note))));

        refusal.Message.ShouldContain(CreatePointer);
        refusal.Message.ShouldContain($"'{field}'");
        refusal.Message.ShouldContain($"'{facet}'");
        refusal.Message.ShouldNotContain("SECRET-VALUE");
        refusal.Message.ShouldNotContain("superadmin");
        refusal.Message.ShouldNotContain("not-an-address");
    }

    [Theory]
    [InlineData(123456.5, "precision")]
    [InlineData(1.125, "scale")]
    public void A_decimal_copied_by_a_mutate_honours_the_target_precision_and_scale(double big, string facet)
    {
        var refusal = Should.Throw<AlvoAuthorizationException>(
            () => Run(BeforeCreate(Mutate("amount", Cel("new.big"))), Row(("big", (decimal)big))));

        refusal.Message.ShouldContain($"'{facet}'");
    }

    [Fact]
    public void An_integer_widened_into_a_decimal_field_is_measured_against_its_precision()
        => Should.Throw<AlvoAuthorizationException>(
            () => Run(BeforeCreate(Mutate("amount", Cel("new.qty"))), Row(("qty", 100_000L))))
            .Message.ShouldContain("'precision'");

    [Fact]
    public void A_host_decimal_result_is_measured_against_the_target_scale()
        => Should.Throw<AlvoAuthorizationException>(
            () => Run(BeforeCreate(Mutate("amount", Cel("third(new.big)"))), Row(("big", 1m))))
            .Message.ShouldContain("'scale'");

    [Fact]
    public void A_mutate_result_inside_every_facet_is_patched_as_before()
    {
        var patch = Run(
            BeforeCreate(Mutate("code", Cel("new.note"))), Row(("note", "abc")));

        patch.ShouldContainKeyAndValue("code", "abc");
    }

    [Fact]
    public void A_declared_enum_value_is_accepted()
        => Run(BeforeCreate(Mutate("stage", Cel("new.note"))), Row(("note", "done")))
            .ShouldContainKeyAndValue("stage", "done");

    [Fact]
    public void A_null_into_an_optional_field_is_still_a_value_to_store()
        => Run(BeforeCreate(Mutate("code", Cel("new.note"))), Row(("note", null)))
            .ShouldContainKeyAndValue("code", null);

    [Fact]
    public void The_refusal_holds_on_an_update_and_names_that_hook()
    {
        var hooks = new EntityHooks
        {
            BeforeUpdate = [new BeforeHook { Action = Mutate("code", Cel("new.note")) }],
        };

        var refusal = Should.Throw<AlvoAuthorizationException>(
            () => Run(hooks, Row(("note", Secret)), DataOperation.Update));

        refusal.Message.ShouldContain("/entities/contacts/hooks/beforeUpdate/0");
        refusal.Message.ShouldNotContain("SECRET-VALUE");
    }

    [Fact]
    public void A_later_hook_may_repair_a_value_an_earlier_hook_overran()
    {
        var hooks = BeforeCreate(Mutate("code", Cel("new.note")), Mutate("code", Literal("\"ok\"")));

        Run(hooks, Row(("note", Secret))).ShouldContainKeyAndValue("code", "ok");
    }

    [Fact]
    public void An_overrun_by_a_later_hook_names_that_hook_and_not_the_one_whose_value_it_replaced()
    {
        var hooks = BeforeCreate(Mutate("code", Literal("\"ok\"")), Mutate("code", Cel("new.note")));

        var refusal = Should.Throw<AlvoAuthorizationException>(() => Run(hooks, Row(("note", Secret))));

        refusal.Message.ShouldContain("/entities/contacts/hooks/beforeCreate/1");
        refusal.Message.ShouldNotContain(CreatePointer);
        refusal.Message.ShouldNotContain("SECRET-VALUE");
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("masked")]
    public void A_refusal_for_a_hidden_target_names_no_field_facet_or_limit(string field)
    {
        var refusal = Should.Throw<AlvoAuthorizationException>(
            () => Run(BeforeCreate(Mutate(field, Cel("new.note"))), Row(("note", Secret))));

        refusal.Message.ShouldContain(CreatePointer);
        refusal.Message.ShouldNotContain(field);
        refusal.Message.ShouldNotContain("max-length");
        refusal.Message.ShouldNotContain("5 characters");
        refusal.Message.ShouldNotContain("SECRET-VALUE");
    }

    [Theory]
    [InlineData("stage", "\"superadmin\"", "enum")]
    [InlineData("code", "\"far-too-long\"", "5 characters")]
    [InlineData("email", "\"nobody\"", "'email' format")]
    [InlineData("amount", "1.125", "fractional digits")]
    public void A_literal_outside_the_target_fields_facets_is_refused_at_apply(
        string field, string json, string expected)
    {
        PolicyCatalog.TryBuild(
            Descriptor(BeforeCreate(Mutate(field, Literal(json)))), Schema, Compiler, out _, out var errors)
            .ShouldBeFalse();

        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe($"{CreatePointer}/action/mutate/{field}");
        error.Message.ShouldContain(expected);
    }

    private static readonly CelFunction _pad = TestCelFunctions.Host(
        "pad", CelValueType.String, _ => new string('x', 5000), TestCelFunctions.Parameter("s", CelValueType.String));

    private static readonly CelFunction _third = TestCelFunctions.Host(
        "third", CelValueType.Decimal, arguments => (decimal)arguments[0]! / 3, TestCelFunctions.Parameter("x", CelValueType.Decimal));

    private static CelCompiler Compiler { get; } = TestCelFunctions.Compiler(_pad, _third);

    private static readonly AlvoContext _caller = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private static AlvoRecord Row(params (string Field, object? Value)[] fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "A title" };
        foreach (var (field, value) in fields)
        {
            values[field] = value;
        }

        return new AlvoRecord(values);
    }

    private static IReadOnlyDictionary<string, object?> Run(
        EntityHooks hooks, AlvoRecord candidate, DataOperation operation = DataOperation.Create)
    {
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(Contacts, PolicyCatalog.Build(Descriptor(hooks), Schema, Compiler));

        return new BeforeHookRunner(provider).Run(
            Contacts, operation, candidate, operation == DataOperation.Create ? null : candidate, _caller, DateTimeOffset.UnixEpoch);
    }

    private static BeforeHookAction Mutate(string field, ValueOrExpr value) => new()
    {
        Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal) { [field] = value },
    };

    private static ValueOrExpr Cel(string source) => ValueOrExpr.FromExpression(source);

    private static ValueOrExpr Literal(string json) => ValueOrExpr.FromLiteral(JsonDocument.Parse(json).RootElement);

    private static EntityHooks BeforeCreate(params BeforeHookAction[] actions) =>
        new() { BeforeCreate = [.. actions.Select(action => new BeforeHook { Action = action })] };

    private static AlvoDescriptor Descriptor(EntityHooks hooks) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = Contacts,
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Contacts] = new() { Fields = HiddenFlags, Hooks = hooks },
        },
    };

    /// <summary>One field hidden statically, one per role — the two shapes a <c>hidden</c> flag takes.</summary>
    private static Dictionary<string, FieldDescriptor> HiddenFlags => new(StringComparer.Ordinal)
    {
        ["secret"] = new()
        {
            Type = MMLib.Alvo.Descriptor.FieldType.String,
            MaxLength = 5,
            Hidden = BoolOrCel.FromBoolean(true),
        },
        ["masked"] = new()
        {
            Type = MMLib.Alvo.Descriptor.FieldType.String,
            MaxLength = 5,
            Hidden = BoolOrCel.FromExpression("!('admin' in @user.roles)"),
        },
    };

    private static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = Contacts,
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = FieldType.String, Required = true, MaxLength = 200 },
                new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true },
                new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 5, Nullable = true },
                new FieldSchema { Name = "stage", Type = FieldType.Enum, EnumValues = ["draft", "done"], Nullable = true },
                new FieldSchema { Name = "email", Type = FieldType.String, Format = "email", Nullable = true },
                new FieldSchema { Name = "amount", Type = FieldType.Decimal, Precision = 5, Scale = 2, Nullable = true },
                new FieldSchema { Name = "big", Type = FieldType.Decimal, Precision = 18, Scale = 4, Nullable = true },
                new FieldSchema { Name = "qty", Type = FieldType.Integer, Nullable = true },
                new FieldSchema { Name = "secret", Type = FieldType.String, MaxLength = 5, Nullable = true },
                new FieldSchema { Name = "masked", Type = FieldType.String, MaxLength = 5, Nullable = true },
            ],
        },
    ]);
}
