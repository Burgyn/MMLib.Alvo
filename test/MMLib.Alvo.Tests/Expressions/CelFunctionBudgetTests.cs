using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The host functions' time budget (#309): one deadline per evaluation pass, a token for a function that takes one, and a
/// measurement on return for every function, so neither ignoring nor catching the token escapes it.
/// </summary>
public sealed class CelFunctionBudgetTests
{
    private readonly FakeTimeProvider _clock = new();

    private static CelFunction Function(Func<string, CancellationToken, string> body) =>
        HostCelFunction.Create("slow", body, summary: null);

    private static CelFunction TokenFree(Func<string, string> body) => HostCelFunction.Create("plain", body, summary: null);

    private CelFunctionBudget Pass() => TestBudgets.Default(_clock).Enter(TestContext.Current.CancellationToken);

    [Fact]
    public void A_token_aware_function_receives_the_pass_s_token_and_its_cancellation_is_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            ct.ThrowIfCancellationRequested();
            return s;
        });
        using var pass = Pass();

        var failure = Should.Throw<CelFunctionException>(() => function.Invoke(["x"]));

        failure.IsHost.ShouldBeTrue();
        failure.FunctionName.ShouldBe("slow");
        failure.Reason.ShouldBe("it ran past the 250 ms that host functions have in one evaluation");
    }

    [Fact]
    public void A_function_that_ignores_the_token_is_measured_when_it_returns()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(251));
            return s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_function_that_catches_its_cancellation_and_returns_is_still_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            try
            {
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return "fallback";
            }

            return s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_function_inside_its_budget_answers_and_its_token_is_live()
    {
        var seen = CancellationToken.None;
        var function = Function((s, ct) =>
        {
            seen = ct;
            _clock.Advance(TimeSpan.FromMilliseconds(249));
            return s + "!";
        });
        using var pass = Pass();

        function.Invoke(["x"]).ShouldBe("x!");
        seen.CanBeCanceled.ShouldBeTrue("the pass hands a real, cancellable token");
        seen.IsCancellationRequested.ShouldBeFalse("one millisecond before the limit is still inside it");
    }

    [Fact]
    public void Exactly_the_limit_is_an_overrun_and_the_token_has_fired()
    {
        var seen = CancellationToken.None;
        var function = Function((s, ct) =>
        {
            seen = ct;
            _clock.Advance(TimeSpan.FromMilliseconds(250));
            return s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
        seen.IsCancellationRequested.ShouldBeTrue("the token and the measurement share one boundary");
    }

    [Fact]
    public void A_function_that_blocks_with_result_past_the_budget_is_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            return Task.Run(() => s, ct).Result;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_function_that_turns_its_cancellation_into_its_own_exception_is_still_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            return ct.IsCancellationRequested ? throw new TimeoutException("rates service") : s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void Two_calls_in_one_pass_share_one_deadline()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(150));
            return s;
        });
        using var pass = Pass();

        function.Invoke(["first"]).ShouldBe("first");
        Should.Throw<CelFunctionException>(() => function.Invoke(["second"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_new_pass_starts_a_new_budget()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(150));
            return s;
        });

        using (Pass())
        {
            function.Invoke(["first"]).ShouldBe("first");
        }

        using (Pass())
        {
            function.Invoke(["second"]).ShouldBe("second");
        }
    }

    [Fact]
    public void The_clock_starts_at_the_first_host_call_not_when_the_pass_opens()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            return s;
        });
        using var pass = Pass();
        _clock.Advance(TimeSpan.FromSeconds(10));

        function.Invoke(["x"]).ShouldBe("x");
    }

    [Fact]
    public void Outside_a_pass_a_function_gets_no_token_and_no_limit()
    {
        var seen = new CancellationTokenSource().Token;
        var function = Function((s, ct) =>
        {
            seen = ct;
            _clock.Advance(TimeSpan.FromSeconds(10));
            return s;
        });

        function.Invoke(["x"]).ShouldBe("x");
        seen.CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public void An_outer_cancellation_reaches_the_token_and_is_the_function_s_failure_not_an_overrun()
    {
        using var outer = new CancellationTokenSource();
        var function = Function((s, ct) =>
        {
            outer.Cancel();
            ct.ThrowIfCancellationRequested();
            return s;
        });
        using var pass = TestBudgets.Default(_clock).Enter(outer.Token);

        var failure = Should.Throw<CelFunctionException>(() => function.Invoke(["x"]));

        failure.Reason.ShouldBeNull("a body that threw keeps no reason; the caller reads the generic sentence");
        failure.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public void Leaving_a_pass_restores_the_one_around_it()
    {
        using var outer = Pass();
        using (Pass())
        {
            CelFunctionBudget.Current.ShouldNotBeSameAs(outer);
        }

        CelFunctionBudget.Current.ShouldBeSameAs(outer);
    }

    [Fact]
    public void The_overrun_reason_names_the_configured_limit()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(41));
            return s;
        });
        using var pass = TestBudgets.Default(_clock, TimeSpan.FromMilliseconds(40)).Enter(TestContext.Current.CancellationToken);

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason
            .ShouldBe("it ran past the 40 ms that host functions have in one evaluation");
    }
}
