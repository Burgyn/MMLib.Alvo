using CsCheck;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Testing.Data;

public abstract partial class AlvoDataDifferentialTests
{
    /// <summary>
    /// One draw of the agreement property (HX-11 acceptance (6)): a rule from a fixed set of duration and <c>now()</c>
    /// shapes over <see cref="DifferentialEntity"/>, a row, and the instant <c>now()</c> reads. <b>Stratified</b> (Ruling
    /// 50): the dependent operand lands on the admitted side of the rule's boundary half the time and on the refused side
    /// otherwise, so neither verdict is rare; every value is millisecond-aligned (Ruling 11).
    /// </summary>
    /// <param name="Rule">The CEL rule.</param>
    /// <param name="Row">The candidate row.</param>
    /// <param name="Now">The instant bound around the render and the evaluation.</param>
    private sealed record TemporalCase(string Rule, AlvoRecord Row, DateTimeOffset Now)
    {
        private const long DayMs = 86_400_000L;
        private const long LatestMs = 4_102_444_800_000L;
        private const long TenYearsMs = 315_360_000_000L;
        private const long MonthMs = 30 * DayMs;

        private static readonly TimeSpan[] _offsets =
            [TimeSpan.Zero, TimeSpan.FromHours(2), new TimeSpan(-5, -30, 0), TimeSpan.FromHours(14)];

        /// <summary>The generator: template, instants, offsets, a duration within ±10 years, the side, and a rare null.</summary>
        public static Gen<TemporalCase> Any { get; } =
            Gen.Select(Gen.Int[0, 6], Gen.Long[0, LatestMs], Gen.Long[0, LatestMs], Gen.Long[-TenYearsMs, TenYearsMs])
                .SelectMany(head => Gen.Select(
                    Gen.Long[1, MonthMs], Gen.Bool, Gen.Int[0, 9], Gen.Int[0, _offsets.Length - 1], Gen.Int[0, _offsets.Length - 1])
                    .Select(tail => new Draw(
                        head.Item1, head.Item2, head.Item3, head.Item4, tail.Item1, tail.Item2, tail.Item3 == 0, tail.Item4, tail.Item5)))
                .Select(Build);

        public override string ToString() =>
            $"'{Rule}' at now={Now:O} over "
            + string.Join(", ", Row.Values.Select(pair => $"{pair.Key}={(pair.Value is IFormattable value ? value.ToString("O", null) : "null")}"));

        private static TemporalCase Build(Draw draw)
        {
            var now = DateTimeOffset.UnixEpoch.AddMilliseconds(draw.NowMs);
            var d = $"duration('{draw.DurationMs}ms')";
            return draw.Template switch
            {
                0 => Pair($"approved_at - created_at <= {d}", draw, now, admitAt: 1 - draw.Delta, refuseAt: draw.Delta),
                1 => Pair($"created_at + {d} < approved_at", draw, now, admitAt: draw.Delta, refuseAt: 1 - draw.Delta),
                2 => Pair($"approved_at - {d} >= created_at", draw, now, admitAt: draw.Delta - 1, refuseAt: -draw.Delta),
                3 => Window($"created_at > now() - {d}", draw, now),
                4 => DateShifted($"due_on + {d} <= created_at", draw, now),
                5 => DateWindow($"due_on > now() - {d}", draw, now),
                _ => Pair($"approved_at - created_at == {d}", draw, now, admitAt: 0, refuseAt: draw.Delta % 2 == 0 ? draw.Delta : -draw.Delta),
            };
        }

        /// <summary><c>created_at</c> is the free operand; <c>approved_at</c> sits at <c>created_at + d</c>, moved to a side.</summary>
        private static TemporalCase Pair(string rule, Draw draw, DateTimeOffset now, long admitAt, long refuseAt)
        {
            var boundary = draw.FreeMs + draw.DurationMs;
            return Of(rule, now, draw, ("created_at", Instant(draw.FreeMs, draw.FreeOffset)), ("approved_at", Instant(boundary + (draw.Admit ? admitAt : refuseAt), draw.DependentOffset)));
        }

        private static TemporalCase Window(string rule, Draw draw, DateTimeOffset now)
        {
            var boundary = draw.NowMs - draw.DurationMs;
            return Of(rule, now, draw, ("created_at", Instant(boundary + (draw.Admit ? draw.Delta : 1 - draw.Delta), draw.DependentOffset)));
        }

        private static TemporalCase DateShifted(string rule, Draw draw, DateTimeOffset now)
        {
            var midnight = FloorDay(draw.FreeMs);
            var boundary = midnight + draw.DurationMs;
            return Of(rule, now, draw, ("due_on", Day(midnight)), ("created_at", Instant(boundary + (draw.Admit ? draw.Delta - 1 : -draw.Delta), draw.DependentOffset)));
        }

        /// <summary>A <c>date</c> against a folded instant (review I10): the first midnight past the boundary, or the last one at or before it.</summary>
        private static TemporalCase DateWindow(string rule, Draw draw, DateTimeOffset now)
        {
            var atOrBefore = FloorDay(draw.NowMs - draw.DurationMs);
            var days = draw.Delta / DayMs * DayMs;
            return Of(rule, now, draw, ("due_on", Day(draw.Admit ? atOrBefore + DayMs + days : atOrBefore - days)));
        }

        private static TemporalCase Of(string rule, DateTimeOffset now, Draw draw, params (string Field, object? Value)[] values)
        {
            if (draw.Empty)
            {
                values[^1] = (values[^1].Field, null);
            }

            return new TemporalCase(rule, new AlvoRecord(values.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal)), now);
        }

        private static DateTimeOffset Instant(long ms, int offset) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms).ToOffset(_offsets[offset]);

        private static DateOnly Day(long midnightMs) => DateOnly.FromDateTime(DateTimeOffset.UnixEpoch.AddMilliseconds(midnightMs).UtcDateTime);

        private static long FloorDay(long ms) => (long)Math.Floor(ms / (double)DayMs) * DayMs;

        private sealed record Draw(
            int Template, long FreeMs, long NowMs, long DurationMs, long Delta, bool Admit, bool Empty, int FreeOffset, int DependentOffset);
    }
}
