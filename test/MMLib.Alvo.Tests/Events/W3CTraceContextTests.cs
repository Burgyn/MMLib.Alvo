using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The W3C Trace Context grammar an envelope's <c>traceparent</c> and <c>tracestate</c> must satisfy before they are
/// stamped: a value that fails it is dropped rather than carried as the registered extension's value.
/// </summary>
public sealed class W3CTraceContextTests
{
    [Theory]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00")]
    public void A_version_00_traceparent_is_valid(string traceParent)
        => W3CTraceContext.IsTraceParent(traceParent).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("not-a-traceparent")]
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-extra")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\n")]
    [InlineData(" 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    public void A_traceparent_outside_the_grammar_is_invalid(string traceParent)
        => W3CTraceContext.IsTraceParent(traceParent).ShouldBeFalse();

    [Theory]
    [InlineData("congo=t61rcWkgMzE")]
    [InlineData("rojo=00f067aa0ba902b7,congo=t61rcWkgMzE")]
    [InlineData("rojo=00f067aa0ba902b7 , \tcongo=t61rcWkgMzE")]
    [InlineData("fw529a3039@dt=ab cd,vendor/x*_-=1")]
    [InlineData("rojo=1,,congo=2")]
    public void A_tracestate_in_the_list_grammar_is_valid(string traceState)
        => W3CTraceContext.IsTraceState(traceState).ShouldBeTrue();

    [Theory]
    [InlineData("congo")]
    [InlineData("Congo=1")]
    [InlineData("congo=a,b")]
    [InlineData("congo=a=b")]
    [InlineData("congo=")]
    [InlineData("congo=1,congo=2")]
    [InlineData("congo=1\n")]
    [InlineData("congo=1\r\nX-Injected: yes")]
    [InlineData("congo=té")]
    public void A_tracestate_outside_the_list_grammar_is_invalid(string traceState)
        => W3CTraceContext.IsTraceState(traceState).ShouldBeFalse();

    [Fact]
    public void A_tracestate_of_more_than_thirty_two_members_is_invalid()
        => W3CTraceContext.IsTraceState(string.Join(',', Enumerable.Range(0, 33).Select(i => $"k{i}=v")))
            .ShouldBeFalse();

    [Fact]
    public void A_tracestate_longer_than_the_cap_is_invalid()
    {
        var atCap = $"k={new string('v', 254)},j={new string('w', 253)}";
        atCap.Length.ShouldBe(W3CTraceContext.MaxTraceStateLength);

        W3CTraceContext.IsTraceState(atCap).ShouldBeTrue();
        W3CTraceContext.IsTraceState(atCap + "x").ShouldBeFalse();
    }
}
