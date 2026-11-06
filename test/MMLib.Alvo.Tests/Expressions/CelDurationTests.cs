using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>CEL's duration grammar (Go's ParseDuration), CEL's range, Alvo's microsecond precision (HX-11 Rulings 7, 8).</summary>
public sealed class CelDurationTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("90m", 5_400_000_000L)]
    [InlineData("1h30m", 5_400_000_000L)]
    [InlineData("-1.5h", -5_400_000_000L)]
    [InlineData("+2s", 2_000_000L)]
    [InlineData("250ms", 250_000L)]
    [InlineData("1us", 1L)]
    [InlineData("1µs", 1L)]
    [InlineData("1μs", 1L)]
    [InlineData("1000ns", 1L)]
    [InlineData(".5s", 500_000L)]
    [InlineData("1.s", 1_000_000L)]
    [InlineData("315576000000s", 315_576_000_000_000_000L)]
    [InlineData("87660000h", 315_576_000_000_000_000L)]
    [InlineData("5259600000m", 315_576_000_000_000_000L)]
    [InlineData("-87660000h", -315_576_000_000_000_000L)]
    [InlineData("000000000000000000001s", 1_000_000L)]
    [InlineData("1.000000000000000000000s", 1_000_000L)]
    public void A_cel_duration_parses_to_its_microseconds(string text, long microseconds)
    {
        CelDuration.TryParse(text, out var value, out var reason).ShouldBeTrue(reason);
        (value.Ticks / 10).ShouldBe(microseconds);
    }

    [Theory]
    [InlineData("", "not a duration")]
    [InlineData("1", "not a duration")]
    [InlineData("1d", "not a duration")]
    [InlineData("h", "not a duration")]
    [InlineData("1h 30m", "not a duration")]
    [InlineData("--1s", "not a duration")]
    [InlineData(".s", "not a duration")]
    [InlineData("1ns", "microsecond")]
    [InlineData("0.5us", "microsecond")]
    [InlineData("315576000001s", "range")]
    [InlineData("-315576000001s", "range")]
    [InlineData("123456789012345678901s", "range")]
    [InlineData("87660001h", "range")]
    [InlineData("5259600001m", "range")]
    [InlineData("1us1ns", "microsecond")]
    [InlineData("0.0000000000000000001s", "microsecond")]
    [InlineData("0.000000000000000000000000000000001s", "microsecond")]
    [InlineData("99999999999999.999999h", "range")]
    public void Anything_else_is_refused_with_its_reason(string text, string reason)
    {
        CelDuration.TryParse(text, out _, out var why).ShouldBeFalse();
        why.ShouldNotBeNull().ShouldContain(reason);
    }

    [Theory]
    [InlineData("90m", "5400s")]
    [InlineData("250ms", "0.25s")]
    [InlineData("-1us", "-0.000001s")]
    [InlineData("0", "0s")]
    public void The_canonical_text_is_seconds(string text, string canonical)
    {
        CelDuration.TryParse(text, out var value, out _).ShouldBeTrue();
        CelDuration.Text(value).ShouldBe(canonical);
        CelDuration.TryParse(canonical, out var again, out _).ShouldBeTrue();
        again.ShouldBe(value);
    }
}
