using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Reflection;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The <see cref="CelProfile.Mutate"/> profile's function allow-list: exactly two entries,
/// <c>lowerAscii(field)</c> and <c>now()</c>, legal in this profile and in no other, with every other
/// identifier followed by <c>(</c> still refused everywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>lowerAscii</c> and not <c>lower</c>.</b> Conformant CEL has no <c>lower(x)</c>; its standard
/// library spells the fold <c>x.lowerAscii()</c>, a receiver-style macro Alvo's grammar cannot express
/// (a field path carries at most one level of <c>old.</c>/<c>new.</c>, so <c>new.email.lowerAscii()</c> is
/// structurally impossible). Alvo therefore adopts the standard's <em>name</em> and its ASCII-only
/// <em>semantics</em> and deviates only on the <em>call shape</em>, exactly as <c>has(...)</c> and
/// <c>changed(...)</c> already do. The name is load-bearing rather than cosmetic: it pins the
/// implementation to folding <c>A</c>–<c>Z</c> and nothing else, so a culture-sensitive fold cannot creep
/// in and write a permanently wrong row.
/// </para>
/// <para>
/// <b>The allow-list is closed on purpose.</b> <c>upper</c>, <c>trim</c>, <c>size</c>, <c>concat</c> and
/// string indexing are all absent because no shipped descriptor uses them, and every entry is a permanent
/// grammar addition every future engine, profile and agent expectation has to carry.
/// </para>
/// </remarks>
public class CelMutateFunctionTests
{
    /// <summary>
    /// The fold touches <c>A</c>–<c>Z</c> and leaves every other code point byte-identical, so a stored
    /// value cannot be rewritten beyond recovery by a fold nobody asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sample string is chosen to kill the obvious wrong implementation.</b> Replacing the fold with
    /// <c>value.ToLowerInvariant()</c> must fail this fact, and picking the trap character by reputation is
    /// not enough to make that true: <c>'İ'</c> (U+0130, LATIN CAPITAL LETTER I WITH DOT ABOVE) is the
    /// famous one, and it is exactly the character .NET's invariant casing leaves <em>alone</em> — measured,
    /// not assumed: <c>"İ".ToLowerInvariant()</c> is U+0130 on .NET 10, unchanged and still one char. A fact
    /// built on <c>İ</c> alone passes under the wrong implementation and proves nothing.
    /// </para>
    /// <para>
    /// So the sample carries the code points <c>ToLowerInvariant</c> really does fold — <c>Ž</c> (U+017D →
    /// U+017E), <c>Ä</c> (U+00C4 → U+00E4), <c>ẞ</c> (U+1E9E → U+00DF, a fold no reverse mapping recovers)
    /// and <c>Σ</c> (U+03A3 → U+03C3) — and keeps <c>İ</c> for what it does document: the fold is
    /// <em>positive</em> (fold A–Z) rather than a list of exceptions, so it is right about characters
    /// nobody thought of, on every runtime and ICU version, including the environments whose full case
    /// mapping turns <c>İ</c> into two code points.
    /// </para>
    /// </remarks>
    [Fact]
    public void LowerAscii_folds_A_to_Z_and_leaves_every_other_code_point_alone()
    {
        Mutate("lowerAscii(new.title)", ("title", "AB.Ž.Ä.ẞ.Σ.İ.Z")).ShouldBe("ab.Ž.Ä.ẞ.Σ.İ.z");
    }

    [Fact]
    public void LowerAscii_of_a_missing_value_stays_missing_rather_than_becoming_an_empty_string()
    {
        Mutate("lowerAscii(new.title)", ("title", null)).ShouldBeNull();
    }

    [Fact]
    public void LowerAscii_compiles_in_the_mutate_profile_as_a_string()
    {
        CelFixtures.CompileMutate("lowerAscii(new.title)").ResultType.ShouldBe(CelValueType.String);
    }

    /// <summary>
    /// The whole reason the standard's name was adopted: an author (or an agent trained on some other
    /// backend's <c>lower()</c>) is told the Alvo spelling rather than left with "not a recognized
    /// function".
    /// </summary>
    [Fact]
    public void Lower_is_refused_and_the_fix_suggestion_names_lowerAscii()
    {
        var refused = Compile("lower(new.title)", CelProfile.Mutate);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain(CelCall.LowerAscii);
    }

    /// <summary>
    /// Enumerated from the enum rather than listed, so a profile added later cannot quietly inherit the
    /// allow-list this fact says belongs to <see cref="CelProfile.Mutate"/> alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryProfileButMutate))]
    public void LowerAscii_is_refused_in_every_profile_but_mutate(CelProfile profile)
    {
        var refused = Compile("lowerAscii(title)", profile);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].Message.ShouldContain(nameof(CelProfile.Mutate));
    }

    public static TheoryData<CelProfile> EveryProfileButMutate()
    {
        TheoryData<CelProfile> profiles = [];
        foreach (var profile in Enum.GetValues<CelProfile>().Where(profile => profile != CelProfile.Mutate))
        {
            profiles.Add(profile);
        }

        return profiles;
    }

    [Fact]
    public void LowerAscii_of_a_non_string_is_refused_at_compile_time_not_left_to_evaluation()
    {
        var refused = Compile("lowerAscii(total)", CelProfile.Mutate);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].Message.ShouldContain("must be a string");
    }

    /// <summary>
    /// The argument is a field reference, never an arbitrary expression — the same narrowing
    /// <c>has(field)</c>/<c>changed(field)</c> already use. Widening it later accepts more source than
    /// before and so cannot break an authored descriptor; starting wide and narrowing later would.
    /// </summary>
    [Fact]
    public void LowerAscii_takes_a_field_reference_and_not_an_arbitrary_expression()
    {
        Compile("lowerAscii('ABC')", CelProfile.Mutate).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void LowerAscii_takes_exactly_one_argument()
    {
        Compile("lowerAscii(title, title)", CelProfile.Mutate).IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// The pressure the allow-list deliberately does not relieve: the next plausible function is refused
    /// like any other identifier, in <see cref="CelProfile.Mutate"/> as everywhere else, so an entry is
    /// added by a named decision rather than by whoever needed one first.
    /// </summary>
    [Fact]
    public void An_unlisted_function_is_still_refused_inside_the_mutate_profile()
    {
        var refused = Compile("upper(new.title)", CelProfile.Mutate);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("hooks.beforeUpdate");
    }

    /// <summary>
    /// <see cref="CelProfile.Mutate"/>'s result-type bar, and the one place it is deliberately looser than
    /// <see cref="CelProfile.Computed"/>'s: a <c>mutate</c> value is assigned to a field and written as a
    /// bound parameter, so a boolean is a legitimate result (a boolean column is a legitimate target) where
    /// <see cref="CelProfile.Computed"/> has to refuse one — a generated column cannot hold "predicate" as
    /// a value.
    /// </summary>
    [Fact]
    public void A_boolean_is_a_legal_mutate_value_even_though_computed_refuses_one()
    {
        CelFixtures.CompileMutate("is_public").ResultType.ShouldBe(CelValueType.Bool);

        CelFixtures.Compiler.Compile("is_public", CelProfile.Computed, CelFixtures.Orders)
            .IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// Everything a column cannot hold is still refused — the bar is "a value a field can hold", not "no
    /// bar at all".
    /// </summary>
    [Theory]
    [InlineData("payload")]
    [InlineData("null")]
    public void A_mutate_value_that_no_field_can_hold_is_refused(string source)
    {
        var refused = Compile(source, CelProfile.Mutate);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].Message.ShouldContain(nameof(CelProfile.Mutate));
    }

    [Fact]
    public void Now_compiles_in_the_mutate_profile_as_a_timestamp()
    {
        CelFixtures.CompileMutate("now()").ResultType.ShouldBe(CelValueType.Timestamp);
    }

    [Theory]
    [MemberData(nameof(EveryProfileButMutate))]
    public void Now_is_refused_in_every_profile_but_mutate(CelProfile profile)
    {
        var refused = Compile("now()", profile);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].Message.ShouldContain(nameof(CelProfile.Mutate));
    }

    [Fact]
    public void Now_takes_no_arguments()
    {
        Compile("now(title)", CelProfile.Mutate).IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// The fact that makes <c>now()</c> not-a-clock-read: the write binds one instant — the same one its
    /// audit stamp uses — and every evaluation in that write reads the bound value, so the wall clock moving
    /// mid-write cannot change the answer. A retry re-stamps, exactly as the candidate row is rebuilt per
    /// attempt; what one attempt cannot do is disagree with itself.
    /// </summary>
    /// <remarks>
    /// This is what makes a <see cref="CelProfile.Mutate"/> expression referentially transparent within a
    /// write, and therefore what makes it safe to evaluate inside a transaction that may be retried. It is
    /// also the fact that dies if the interpreter ever reads a clock itself — verified by mutation, since
    /// <see cref="The_interpreter_is_handed_an_instant_and_never_a_clock_to_read"/> only pins the signature
    /// and cannot see into a method body.
    /// </remarks>
    [Fact]
    public void Now_is_the_writes_bound_instant_and_not_a_fresh_read()
    {
        var clock = new MovableClock(_stamp);
        var bound = clock.GetUtcNow();             // the write stamps itself once, from the clock port
        clock.Advance(TimeSpan.FromMinutes(5));    // the wall clock then moves, mid-write

        MutateAt("now()", bound).ShouldBe(_stamp);  // and the answer does not
    }

    [Fact]
    public void Now_is_the_same_instant_for_every_evaluation_in_one_write()
    {
        var bound = new MovableClock(_stamp).GetUtcNow();

        MutateAt("now()", bound).ShouldBe(MutateAt("now()", bound));
        MutateAt("now()", bound).ShouldBe(_stamp);
    }

    /// <summary>
    /// The structural half of the same rule: the interpreter is <em>handed</em> an instant and is never
    /// handed a clock, so the caller that already computed the write's audit stamp is the only thing that
    /// reads time. A signature fact cannot see a <c>TimeProvider.System</c> call inside a method body — the
    /// bound-instant fact above is what catches that — but it does stop the seam from being widened into one.
    /// </summary>
    [Fact]
    public void The_interpreter_is_handed_an_instant_and_never_a_clock_to_read()
    {
        typeof(CelInterpreter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .ShouldNotContain(parameter => parameter.ParameterType == typeof(TimeProvider));
    }

    /// <summary>
    /// A timestamp is not a string, so the fold refuses it — and the refusal is the type checker's, at
    /// compile time, rather than a <see langword="null"/> discovered mid-write.
    /// </summary>
    [Fact]
    public void LowerAscii_of_now_is_refused_because_a_timestamp_is_not_a_string()
    {
        Compile("lowerAscii(now())", CelProfile.Mutate).IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// A fixed instant far from any wall clock, so a fact that accidentally measured
    /// <c>TimeProvider.System</c> could never coincide with it.
    /// </summary>
    private static readonly DateTimeOffset _stamp = new(2000, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        CelFixtures.Compiler.Compile(source, profile, CelFixtures.Orders);

    private static object? Mutate(string source, params (string Field, object? Value)[] candidate) =>
        CelInterpreter.EvaluateMutation(CelFixtures.CompileMutate(source), CelFixtures.Row(candidate), null, _stamp);

    private static object? MutateAt(string source, DateTimeOffset now) =>
        CelInterpreter.EvaluateMutation(CelFixtures.CompileMutate(source), AlvoRecord.Empty, null, now);

    /// <summary>
    /// A hand-rolled movable clock. <c>FakeTimeProvider</c> would be a new package reference for one fact,
    /// and the only thing these facts need from a clock is "it was read once, and it moved afterwards".
    /// </summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
