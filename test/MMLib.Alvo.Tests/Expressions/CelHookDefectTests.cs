using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions.Internal;
using System.Collections;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The defence-in-depth catch at <see cref="CelInterpreter.EvaluatePredicate"/> and
/// <see cref="CelInterpreter.EvaluateMutation"/> (Ruling Y-D, final review M1). On the fail-closed path — a hook's
/// Condition or Mutate — an exception nothing anticipated is a defect, and answering <see langword="false"/> would
/// switch a reject off silently while <see langword="null"/> would write a column default. It is wrapped as a
/// <see cref="CelFunctionException"/> instead, so the write rolls back as <c>function-failed</c> and the original is
/// kept for the log. A Rule keeps its old answer: <see langword="false"/> is a deny there, which is already closed.
/// </summary>
public sealed class CelHookDefectTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static AlvoRecord Poisoned => new(new ThrowingFieldSource());

    [Fact]
    public void An_unexpected_exception_in_a_condition_fails_closed_with_the_original_kept()
    {
        var failure = Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluatePredicate(CelFixtures.CompileCondition("title == 'x'"), Poisoned, previous: null, CelFixtures.Alice));

        failure.FunctionName.ShouldBe(CelInterpreter.HookEvaluation);
        failure.Reason.ShouldBe(CelInterpreter.HookEvaluationReason);
        failure.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void An_unexpected_exception_in_a_mutate_fails_closed_with_the_original_kept()
    {
        var failure = Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluateMutation(CelFixtures.CompileMutate("title"), Poisoned, previous: null, _now));

        failure.FunctionName.ShouldBe(CelInterpreter.HookEvaluation);
        failure.Reason.ShouldBe(CelInterpreter.HookEvaluationReason);
        failure.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void The_wrapped_reason_names_no_value()
    {
        var failure = Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluatePredicate(CelFixtures.CompileCondition("title == 'x'"), Poisoned, previous: null, CelFixtures.Alice));

        failure.Message.ShouldNotContain(ThrowingFieldSource.Secret);
        failure.Reason!.ShouldNotContain(ThrowingFieldSource.Secret);
    }

    [Fact]
    public void An_unexpected_exception_in_a_rule_still_answers_false() =>
        CelInterpreter.EvaluatePredicate(CelFixtures.CompileRule("title == 'x'"), Poisoned, previous: null, CelFixtures.Alice)
            .ShouldBeFalse();

    [Fact]
    public void An_unexpected_exception_in_a_computed_value_still_answers_null() =>
        CelInterpreter.EvaluateScalar(CelFixtures.CompileComputed("title"), Poisoned).ShouldBeNull();

    /// <summary>An <see cref="IReadOnlyDictionary{TKey,TValue}"/> that throws from every member, standing in for a defect.</summary>
    private sealed class ThrowingFieldSource : IReadOnlyDictionary<string, object?>
    {
        internal const string Secret = "row value 4711";

        public object? this[string key] => throw Defect();

        public IEnumerable<string> Keys => throw Defect();

        public IEnumerable<object?> Values => throw Defect();

        public int Count => throw Defect();

        public bool ContainsKey(string key) => throw Defect();

        public bool TryGetValue(string key, out object? value) => throw Defect();

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw Defect();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static InvalidOperationException Defect() => new($"A defect while reading {Secret}.");
    }
}
