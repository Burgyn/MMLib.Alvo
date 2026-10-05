using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// CEL's <c>+</c> over two strings, admitted in the <see cref="CelProfile.Computed"/> profile only — the rung a
/// <c>full_name = first_name + ' ' + last_name</c> belongs on (assistant-reliability design, ruling 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The CEL spec's own overloads, nothing invented.</b> <c>_+_</c> is defined for <c>(string, string) → string</c>
/// and for the numeric pairs; there is no <c>(int, string)</c> overload and CEL converts nothing implicitly, so a
/// mixed pair is a type error rather than a coercion.
/// </para>
/// <para>
/// <b>The null rule is a refusal.</b> CEL's <c>+</c> has no null overload, and SQL's <c>||</c> makes the whole value
/// <c>NULL</c> when any operand is. A nullable operand is therefore refused at compile time unless the author writes
/// the fallback with the profile's own coalescing construct, <c>has(f) ? f : '…'</c> — so the two semantics never
/// get the chance to disagree.
/// </para>
/// </remarks>
public class CelStringConcatenationTests
{
    internal static EntitySchema Customers { get; } = new()
    {
        Name = "customers",
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "first_name", Type = FieldType.String, MaxLength = 60, Required = true },
            new FieldSchema { Name = "last_name", Type = FieldType.String, MaxLength = 60, Required = true },
            new FieldSchema { Name = "middle_name", Type = FieldType.String, MaxLength = 40, Nullable = true },
            new FieldSchema { Name = "notes", Type = FieldType.Text, Nullable = true },
            new FieldSchema { Name = "tier", Type = FieldType.Enum, EnumValues = ["none", "club"], Required = true },
            new FieldSchema { Name = "visits", Type = FieldType.Integer, Required = true },
        ],
    };

    /// <summary>The maintainer's own case, left to right: <c>(first_name + ' ') + last_name</c>, a string.</summary>
    [Theory]
    [InlineData("first_name + ' ' + last_name")]
    [InlineData("first_name + last_name")]
    [InlineData("last_name + ', ' + first_name")]
    [InlineData("tier + '/' + first_name")]
    [InlineData("(has(middle_name) ? middle_name : '') + last_name")]
    [InlineData("first_name + (!has(middle_name) ? '' : middle_name)")]
    public void Two_strings_joined_by_plus_are_a_string_in_the_computed_profile(string source)
    {
        var result = Compile(source, CelProfile.Computed);

        result.IsSuccess.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Expression!.ResultType.ShouldBe(CelValueType.String);
    }

    /// <summary>
    /// No implicit conversion, per the spec. The fix says so — and that this build has no <c>string()</c> in a
    /// computed field — rather than pointing at a conversion the profile would then refuse.
    /// </summary>
    [Theory]
    [InlineData("first_name + visits")]
    [InlineData("visits + first_name")]
    [InlineData("first_name + 1")]
    [InlineData("first_name + id")]
    public void A_string_and_a_non_string_are_a_type_error_not_a_coercion(string source)
    {
        var error = Compile(source, CelProfile.Computed).Errors.ShouldHaveSingleItem();

        error.Message.ShouldContain("'+'");
        error.Message.ShouldContain("String");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("string()");
    }

    /// <summary>The other three operators stay numeric: <c>'a' - 'b'</c> has no CEL overload either.</summary>
    [Theory]
    [InlineData("first_name - last_name")]
    [InlineData("first_name * last_name")]
    [InlineData("first_name / last_name")]
    public void Only_plus_joins_strings(string source)
        => Compile(source, CelProfile.Computed).Errors.ShouldNotBeEmpty();

    /// <summary>
    /// Deny by default: concatenation is a construct of its own in the profile table, admitted where a generated
    /// column renders it and in a mutate value (whose null operand makes the value null, see
    /// <see cref="CelMutateConcatenationTests"/>) — a rule or a hook condition has no use for it.
    /// </summary>
    [Theory]
    [InlineData(CelProfile.Rule)]
    [InlineData(CelProfile.Condition)]
    public void Concatenation_outside_the_computed_profile_is_refused(CelProfile profile)
    {
        var result = Compile("first_name + last_name == 'Jana Nováková'", profile);

        result.Errors.ShouldContain(error => error.Message.Contains("concatenation", StringComparison.Ordinal));
    }

    /// <summary>
    /// The null rule. An optional operand is refused, naming it, and the fix spells the explicit fallback in the
    /// profile's own syntax — the one shape the refusal accepts.
    /// </summary>
    [Theory]
    [InlineData("first_name + middle_name", "middle_name")]
    [InlineData("middle_name + ' ' + last_name", "middle_name")]
    [InlineData("first_name + ' ' + notes", "notes")]
    public void A_nullable_operand_is_refused_with_the_explicit_fallback_as_the_fix(string source, string nullable)
    {
        var error = Compile(source, CelProfile.Computed).Errors.ShouldHaveSingleItem();

        error.Message.ShouldContain($"'{nullable}'");
        error.Message.ShouldContain("null");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain($"has({nullable}) ? {nullable} : ''");
    }

    /// <summary>
    /// A presence test guards only the field it names, and only on the branch it guards: the wrong field, or the
    /// field on the unguarded branch, is still nullable.
    /// </summary>
    [Theory]
    [InlineData("(has(notes) ? middle_name : '') + last_name")]
    [InlineData("(has(middle_name) ? '' : middle_name) + last_name")]
    [InlineData("(visits > 1 ? middle_name : '') + last_name")]
    public void A_fallback_that_does_not_guard_the_operand_is_still_refused(string source)
        => Compile(source, CelProfile.Computed).IsSuccess.ShouldBeFalse();

    /// <summary>
    /// The idiomatic "separator only when present" shapes: a field is known present anywhere inside the branch its
    /// own presence test guards, however deep the join that reads it sits there.
    /// </summary>
    [Theory]
    [InlineData("first_name + (has(middle_name) ? ' ' + middle_name : '') + ' ' + last_name")]
    [InlineData("has(middle_name) ? first_name + ' ' + middle_name : first_name")]
    [InlineData("!has(middle_name) ? first_name : first_name + ' ' + middle_name")]
    [InlineData("has(middle_name) ? (has(notes) ? middle_name + notes : middle_name) : ''")]
    public void A_field_is_known_present_inside_the_branch_its_presence_test_guards(string source)
    {
        var result = Compile(source, CelProfile.Computed);

        result.IsSuccess.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    /// <summary>
    /// The guard's reach ends where its branch does: the other branch, a different field's test, and a read after
    /// the ternary are all still unguarded.
    /// </summary>
    [Theory]
    [InlineData("has(middle_name) ? first_name : first_name + ' ' + middle_name")]
    [InlineData("has(notes) ? first_name + ' ' + middle_name : first_name")]
    [InlineData("!has(middle_name) ? first_name + ' ' + middle_name : first_name")]
    [InlineData("(has(middle_name) ? middle_name : '') + middle_name")]
    [InlineData("(visits > 1 ? ' ' + middle_name : '') + last_name")]
    public void A_read_the_presence_test_does_not_guard_is_still_refused(string source)
    {
        var error = Compile(source, CelProfile.Computed).Errors.ShouldHaveSingleItem();

        error.Message.ShouldContain("'middle_name'");
    }

    /// <summary>
    /// Two independent optional operands are two refusals — one round trip, every finding — and a refused inner
    /// join is not reported again by the join around it.
    /// </summary>
    [Fact]
    public void Every_nullable_operand_is_reported_once()
    {
        var errors = Compile("middle_name + ' ' + notes", CelProfile.Computed).Errors;

        errors.Count.ShouldBe(2);
        errors.ShouldContain(error => error.Message.Contains("'middle_name'", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Message.Contains("'notes'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A text constant in a computed field is written into the column's DDL, which carries no control character and
    /// no line or paragraph separator —
    /// refused at compile time in engine-neutral words, before any dialect is asked to quote it.
    /// </summary>
    [Theory]
    [InlineData("\\n")]
    [InlineData("\\t")]
    [InlineData("\\r")]
    [InlineData(null, 0x00)]
    [InlineData(null, 0x01)]
    [InlineData(null, 0x1B)]
    [InlineData(null, 0x7F)]
    [InlineData(null, 0x85)]
    [InlineData(null, 0x2028)]
    [InlineData(null, 0x2029)]
    [InlineData(null, 0xD800)]
    [InlineData(null, 0xDFFF)]
    public void A_text_constant_carrying_a_control_character_is_refused(string? celEscape, int codeUnit = 0)
    {
        var constant = celEscape ?? ((char)codeUnit).ToString();

        var error = Compile($"first_name + '{constant}' + last_name", CelProfile.Computed).Errors.ShouldHaveSingleItem();

        error.Message.ShouldContain("DDL");
        error.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>The same constant in a rule is a bound parameter, never DDL, so the rule is untouched by it.</summary>
    [Fact]
    public void A_control_character_in_a_rule_literal_is_not_the_computed_refusal()
        => Compile("first_name == 'a\\nb'", CelProfile.Rule).IsSuccess.ShouldBeTrue();

    /// <summary>
    /// The interpreter agrees with the SQL: two strings concatenate, and a null operand — unreachable through the
    /// compiler for a concatenation, but the interpreter answers every well-typed tree — is null, as <c>||</c> is.
    /// </summary>
    [Fact]
    public void The_interpreter_concatenates_the_same_way()
    {
        var expression = Compile("first_name + ' ' + last_name", CelProfile.Computed).Expression!;

        CelInterpreter.EvaluateScalar(expression, CelFixtures.Row(("first_name", "Jana"), ("last_name", "Nováková")))
            .ShouldBe("Jana Nováková");
        CelInterpreter.EvaluateScalar(expression, CelFixtures.Row(("first_name", "Jana"), ("last_name", null)))
            .ShouldBeNull();
    }

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        CelFixtures.Compiler.Compile(source, profile, Customers);
}
