using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>The findings an operator sees under an expression input while typing it (focus-free, latest wins).</summary>
public sealed class ExpressionCheckTests
{
    [Fact]
    public async Task The_latest_source_wins_even_when_an_older_check_answers_last()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        var slow = new TaskCompletionSource<ManagementExpressionVerdict?>();

        var first = sut.SubmitAsync("rule-list", "a", (_, _) => slow.Task);
        await sut.SubmitAsync("rule-list", "b", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("b is wrong")));
        slow.SetResult(new ManagementExpressionVerdict([]));
        await first;

        sut.Findings("rule-list").Single().Message.ShouldBe("b is wrong");
    }

    [Fact]
    public async Task A_check_that_failed_shows_nothing_and_throws_nothing()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };

        await sut.SubmitAsync("rule-list", "a", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(null));

        sut.Findings("rule-list").ShouldBeEmpty();
    }

    [Fact]
    public async Task Findings_clear_when_the_source_becomes_empty()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        await sut.SubmitAsync("k", "x", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("no")));

        await sut.SubmitAsync("k", "", (_, _) => throw new InvalidOperationException("no call for an empty source"));

        sut.Findings("k").ShouldBeEmpty();
    }

    [Fact]
    public async Task Describing_an_input_points_at_its_sentence_only_while_there_is_one()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        sut.DescribedBy("k").ShouldBeNull();

        await sut.SubmitAsync("k", "x", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("no")));

        sut.DescribedBy("k").ShouldBe("k-check");
    }

    [Fact]
    public void The_default_debounce_is_the_measured_three_hundred_milliseconds()
    {
        ExpressionCheck.Debounce.ShouldBe(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task A_change_is_announced_when_findings_arrive()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        var changes = 0;
        sut.Changed += () => changes++;

        await sut.SubmitAsync("k", "x", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("no")));

        changes.ShouldBe(1);
    }

    [Fact]
    public async Task A_check_superseded_while_in_flight_has_its_token_cancelled_and_throws_nothing()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        var slow = new TaskCompletionSource<ManagementExpressionVerdict?>();
        CancellationToken seen = default;

        var first = sut.SubmitAsync("k", "a", (_, ct) =>
        {
            seen = ct;
            return slow.Task;
        });
        seen.IsCancellationRequested.ShouldBeFalse();
        await sut.SubmitAsync("k", "b", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("b is wrong")));

        seen.IsCancellationRequested.ShouldBeTrue();
        slow.SetCanceled(seen);
        await first;
        sut.Findings("k").Single().Message.ShouldBe("b is wrong");
    }

    [Fact]
    public async Task A_submit_superseded_during_the_debounce_never_asks()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.FromHours(1) };
        var asked = false;

        var first = sut.SubmitAsync("k", "a", (_, _) =>
        {
            asked = true;
            return Task.FromResult<ManagementExpressionVerdict?>(null);
        });
        await sut.SubmitAsync("k", "", (_, _) => throw new InvalidOperationException("no call for an empty source"));
        await first;

        asked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_warning_is_kept_with_its_severity_and_describes_the_input()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        var warning = new ManagementExpressionVerdict(
            [new DescriptorValidationError("/p", "careful", null, DescriptorValidationSeverity.Warning)]);

        await sut.SubmitAsync("k", "x", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(warning));

        sut.Findings("k").Single().Severity.ShouldBe(DescriptorValidationSeverity.Warning);
        sut.DescribedBy("k").ShouldBe("k-check");
    }

    private static ManagementExpressionVerdict Refusal(string message) => new(
        [new DescriptorValidationError("/p", message, "fix", DescriptorValidationSeverity.Error)]);
}
