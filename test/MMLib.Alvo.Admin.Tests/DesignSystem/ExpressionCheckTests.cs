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

    private static ManagementExpressionVerdict Refusal(string message) => new(
        [new DescriptorValidationError("/p", message, "fix", DescriptorValidationSeverity.Error)]);
}
