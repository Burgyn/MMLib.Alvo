using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// What apply would say about each expression input on a screen, held per input and refreshed as the operator types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Latest wins.</b> Every submit takes a new generation for its input; an answer is kept only while its
/// generation is still the newest, so a slow answer to an older source can never overwrite the verdict on what is
/// in the box now. A submit that is already stale when its debounce ends never asks at all.
/// </para>
/// <para>
/// <b>Focus-free by construction.</b> There is no JS interop here, and this deliberately does not use
/// <see cref="FieldRefusals"/>: that takes focus on every refusal, which a check that runs while the operator
/// types must never do. The component draws the findings as <c>.a-field__problem</c> markup.
/// </para>
/// <para>
/// A check that could not be asked (a <see langword="null"/> verdict) stores nothing: a helper is never the
/// reason an operator cannot edit.
/// </para>
/// </remarks>
internal sealed class ExpressionCheck
{
    private static readonly IReadOnlyList<DescriptorValidationError> _noFindings = [];

    private readonly Dictionary<string, (int Generation, IReadOnlyList<DescriptorValidationError> Findings)> _inputs = [];

    /// <summary>Raised when the findings of any input changed.</summary>
    public event Action? Changed;

    /// <summary>Gets the default quiet time before a check is asked: the measured 300 ms (p95 of a check is about 3 ms).</summary>
    public static TimeSpan Debounce { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Gets the quiet time to use instead of <see cref="Debounce"/>, for tests.</summary>
    public TimeSpan? DebounceOverride { get; init; }

    /// <summary>The findings on show for one input, or none.</summary>
    /// <param name="key">The input's key.</param>
    /// <returns>The findings, errors and warnings alike; the component styles by severity.</returns>
    public IReadOnlyList<DescriptorValidationError> Findings(string key)
        => _inputs.TryGetValue(key, out var input) ? input.Findings : _noFindings;

    /// <summary>The id of the sentence that describes the input, or <see langword="null"/> while it has none.</summary>
    /// <param name="key">The input's key.</param>
    /// <returns>The id for <c>aria-describedby</c>.</returns>
    public string? DescribedBy(string key) => Findings(key).Count > 0 ? $"{key}-check" : null;

    /// <summary>Checks the source of one input once the operator pauses, unless a newer source arrived since.</summary>
    /// <param name="key">The input's key.</param>
    /// <param name="source">The expression as it stands.</param>
    /// <param name="check">Asks the check; <see langword="null"/> when it could not be asked.</param>
    /// <returns>A task that ends when this submit is settled, answered or abandoned.</returns>
    public async Task SubmitAsync(
        string key, string source, Func<string, CancellationToken, Task<ManagementExpressionVerdict?>> check)
    {
        var generation = NextGeneration(key);

        if (string.IsNullOrWhiteSpace(source))
        {
            Store(key, generation, _noFindings);
            return;
        }

        await Task.Delay(DebounceOverride ?? Debounce).ConfigureAwait(false);
        if (!IsCurrent(key, generation))
        {
            return;
        }

        var verdict = await check(source, CancellationToken.None).ConfigureAwait(false);
        if (verdict is not null && IsCurrent(key, generation))
        {
            Store(key, generation, verdict.Findings);
        }
    }

    private int NextGeneration(string key)
    {
        _inputs.TryGetValue(key, out var input);
        var generation = input.Generation + 1;
        _inputs[key] = (generation, input.Findings ?? _noFindings);
        return generation;
    }

    private bool IsCurrent(string key, int generation) => _inputs[key].Generation == generation;

    private void Store(string key, int generation, IReadOnlyList<DescriptorValidationError> findings)
    {
        _inputs[key] = (generation, findings);
        Changed?.Invoke();
    }
}
