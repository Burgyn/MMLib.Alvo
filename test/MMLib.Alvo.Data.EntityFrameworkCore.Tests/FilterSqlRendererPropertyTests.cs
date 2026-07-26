using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;
using System.Buffers;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// §2.4's <i>"property-based testy dokazujú, že preklad … nikdy neinterpoluje užívateľský vstup"</i> and
/// §2.1's <i>"injection cez každý operátor … fuzzing filtra bez pádu"</i>, over the caller-filter half of
/// the translation. The CEL half is proved by <c>NoInterpolationPropertyTests</c> in the core.
/// </summary>
/// <remarks>
/// <para>
/// Every loop asserts a <b>non-vacuity counter</b> afterwards. A property test whose generator cannot reach
/// the code under test is worse than no test — it reports a guarantee it never exercised — and PR1 shipped
/// exactly that failure once. So each arm records what it actually rendered and what it actually refused, and
/// fails if an operator was never attempted or if nothing ever reached the renderer.
/// </para>
/// <para>
/// The per-operator arm compares the rendered text against a <b>baseline rendered from a benign value</b>
/// rather than searching for the value as a substring: a one-character generated value such as <c>0</c> or a
/// bare space occurs inside <c>@alvo_f0</c> and inside the renderer's own structural text, so a substring
/// search would fail on inputs the generator was designed to produce. Proving the text is byte-identical
/// whatever the value is, is both stronger and free of false positives.
/// </para>
/// </remarks>
public class FilterSqlRendererPropertyTests
{
    private const string Payload = "x'; DROP TABLE vehicle; --";

    /// <summary>
    /// Iterated from the enum, not from a hand-kept list: a new operator cannot be added without this suite
    /// attempting an injection through it.
    /// </summary>
    private static readonly AlvoFilterOperator[] _everyOperator = Enum.GetValues<AlvoFilterOperator>();

    /// <summary>
    /// Characters the renderer never emits structurally, so their presence in a rendered statement can only
    /// mean a caller's value leaked into the text. <c>"</c> is deliberately absent — it delimits a quoted
    /// identifier — and so are the digits, spaces, parentheses and <c>-</c> the structure itself uses.
    /// </summary>
    private static readonly SearchValues<char> _neverStructural =
        SearchValues.Create(['\'', ';', '%', '\\', 'é', '中']);

    private static readonly Gen<string> _hostileText =
        Gen.Char["abcXYZ01_ '\"%;-()\\é中"].Array[1, 24].Select(characters => new string(characters));

    [Fact]
    public void No_filter_value_ever_appears_in_the_rendered_sql()
    {
        var baselines = Baselines();
        var rendered = new ConcurrentDictionary<AlvoFilterOperator, int>();
        var refused = new ConcurrentDictionary<AlvoFilterOperator, int>();

        Gen.Select(_hostileText, Gen.OneOfConst(_everyOperator)).Sample(
            (value, op) =>
            {
                if (!TryRender(new AlvoComparison("status", op, value), out var sql, out var parameters))
                {
                    refused.AddOrUpdate(op, 1, static (_, count) => count + 1);
                    return true;
                }

                rendered.AddOrUpdate(op, 1, static (_, count) => count + 1);
                return sql == baselines[op] && parameters!.Values.Contains(value);
            },
            iter: 10_000);

        EveryOperatorWasAttempted(rendered, refused);
        rendered.Values.Sum().ShouldBeGreaterThan(1_000, "the generator must reach the renderer, not only its refusals.");
    }

    [Fact]
    public void An_injection_attempt_through_every_operator_stays_inside_a_parameter()
    {
        var rendered = 0;

        foreach (var op in _everyOperator)
        {
            if (!TryRender(new AlvoComparison("status", op, Payload), out var sql, out var parameters))
            {
                continue;
            }

            rendered++;
            sql!.ShouldNotContain("DROP", Case.Insensitive);
            parameters!.Values.ShouldContain(Payload);
        }

        rendered.ShouldBeGreaterThan(0, "at least one operator must actually render a caller value.");
    }

    /// <summary>
    /// The same payload wrapped in a list, so the membership operator — the one that renders a value per
    /// candidate — is exercised rather than skipped as "refused".
    /// </summary>
    [Fact]
    public void An_injection_attempt_through_membership_stays_inside_one_parameter_per_candidate()
    {
        var rendered = Render(new AlvoComparison("status", AlvoFilterOperator.In, new object?[] { Payload, Payload + "2" }));

        rendered.Sql.ShouldBe("\"status\" IN (@alvo_f0, @alvo_f1)");
        rendered.Sql.ShouldNotContain("DROP", Case.Insensitive);
        rendered.Parameters.Values.ShouldContain(Payload);
    }

    [Fact]
    public void An_injection_attempt_through_a_field_name_is_refused_for_every_operator()
    {
        foreach (var op in _everyOperator)
        {
            Should.Throw<AlvoAuthorizationException>(
                () => Render(new AlvoComparison("status\"; DROP TABLE vehicle; --", op, "x")),
                $"'{op}' must refuse an undeclared field name before it reaches the SQL text.");
        }
    }

    /// <summary>
    /// The fuzz arm: random trees of random shape over every operator and a hostile value alphabet must
    /// either render or raise one of the two documented refusals — never crash, never overflow the stack,
    /// and never let a value into the statement text.
    /// </summary>
    [Fact]
    public void A_randomly_generated_filter_tree_either_renders_or_is_refused_but_never_crashes()
    {
        var leaf = Gen.Select(_hostileText, Gen.OneOfConst(_everyOperator),
            (value, op) => (AlvoFilter)new AlvoComparison("status", op, value));
        var tree = Gen.Recursive<AlvoFilter>((depth, self) =>
            depth >= 6
                ? leaf
                : Gen.Frequency(
                    (4, leaf),
                    (2, self.Array[0, 4].Select(children => (AlvoFilter)new AlvoAnd(children))),
                    (2, self.Array[0, 4].Select(children => (AlvoFilter)new AlvoOr(children))),
                    (1, self.Select(child => (AlvoFilter)new AlvoNot(child)))));
        var rendered = 0;

        tree.Sample(
            filter =>
            {
                if (!TryRender(filter, out var sql, out _))
                {
                    return true;
                }

                Interlocked.Increment(ref rendered);
                return sql!.AsSpan().IndexOfAny(_neverStructural) < 0;
            },
            iter: 5_000);

        rendered.ShouldBeGreaterThan(1_000, "the generated trees must actually render, not only be refused.");
    }

    /// <summary>
    /// One rendering per operator over a value carrying no character the renderer's own structure uses, so
    /// the per-operator arm has something byte-identical to compare against.
    /// </summary>
    private static Dictionary<AlvoFilterOperator, string> Baselines()
    {
        var baselines = new Dictionary<AlvoFilterOperator, string>();
        foreach (var op in _everyOperator)
        {
            if (TryRender(new AlvoComparison("status", op, "benign"), out var sql, out _))
            {
                baselines[op] = sql!;
            }
        }

        return baselines;
    }

    private static void EveryOperatorWasAttempted(
        ConcurrentDictionary<AlvoFilterOperator, int> rendered, ConcurrentDictionary<AlvoFilterOperator, int> refused)
    {
        foreach (var op in _everyOperator)
        {
            (rendered.ContainsKey(op) || refused.ContainsKey(op)).ShouldBeTrue(
                $"'{op}' was never generated, so this property never covered it.");
        }
    }

    private static bool TryRender(
        AlvoFilter filter, out string? sql, out IReadOnlyDictionary<string, object?>? parameters)
    {
        try
        {
            var rendered = Render(filter);
            sql = rendered.Sql;
            parameters = rendered.Parameters;
            return true;
        }
        catch (AlvoAuthorizationException)
        {
            sql = null;
            parameters = null;
            return false;
        }
        catch (ArgumentException)
        {
            sql = null;
            parameters = null;
            return false;
        }
    }

    private static RenderedSql Render(AlvoFilter filter) => FilterSqlRenderer.Render(
        filter, AlvoDataFixtures.Vehicle, new TestFieldSqlRenderer(), PolicyParameterPrefix.Filter);
}
