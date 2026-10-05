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
/// in the box now. A newer submit also cancels the token of the older one — whether it is still in its debounce
/// (so it never asks) or already in flight — and an older submit so cancelled ends quietly.
/// </para>
/// <para>
/// <b>Focus-free by construction.</b> There is no JS interop here, and this deliberately does not use
/// <see cref="FieldRefusals"/>: that takes focus on every refusal, which a check that runs while the operator
/// types must never do. The component draws the findings as <c>.a-field__problem</c> markup.
/// </para>
/// <para>
/// <b>Threading.</b> Continuations are not moved off the caller's synchronization context (no
/// <c>ConfigureAwait(false)</c>), so state is mutated and <see cref="Changed"/> raised on the context
/// <see cref="SubmitAsync"/> was awaited on — a circuit's, in the dashboard. A component marshals with
/// <c>InvokeAsync(StateHasChanged)</c> and unsubscribes in its own <c>Dispose</c>.
/// </para>
/// <para>
/// A check that could not be asked (a <see langword="null"/> verdict) shows nothing — it clears what the input
/// showed rather than keep it: those findings were about a source no longer in the box, and a stale finding is
/// worse than none (spec §4.3, §5 criterion 4). The previous findings stay on show only while the newer submit is
/// still being asked, so typing does not flicker. A helper is never the reason an operator cannot edit.
/// </para>
/// </remarks>
internal sealed class ExpressionCheck
{
    private static readonly IReadOnlyList<DescriptorValidationError> _noFindings = [];

    private readonly Dictionary<string, Input> _inputs = [];

    /// <summary>Raised when the findings of any input changed, on the context <see cref="SubmitAsync"/> was awaited on.</summary>
    /// <remarks>A subscriber marshals with <c>InvokeAsync(StateHasChanged)</c> and unsubscribes in its own <c>Dispose</c>.</remarks>
    public event Action? Changed;

    /// <summary>Gets the default quiet time before a check is asked: the measured 300 ms (p95 of a check is about 3 ms).</summary>
    public static TimeSpan Debounce { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Gets the quiet time to use instead of <see cref="Debounce"/>, for tests.</summary>
    public TimeSpan? DebounceOverride { get; init; }

    /// <summary>
    /// The start of the sentence that says an input has not been checked because another part of the draft is invalid.
    /// A copy of the core's own prefix (this project cannot reference it); a test pins both to the same text.
    /// </summary>
    internal const string NotJudgedPrefix = "Not checked yet";

    /// <summary>The class of one finding's sentence: red for a problem, muted for "not checked yet", which is not one.</summary>
    /// <param name="finding">The finding on show.</param>
    /// <returns>The class attribute value.</returns>
    public static string ProblemClass(DescriptorValidationError finding) =>
        finding.Message.StartsWith(NotJudgedPrefix, StringComparison.Ordinal)
            ? "a-field__problem a-field__problem--muted"
            : "a-field__problem";

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
    /// <remarks>
    /// A newer submit for the same key cancels this one's token; the cancellation is swallowed (superseded, nothing
    /// stored). A <paramref name="check"/> that throws anything else faults the returned task and leaves the state
    /// consistent — the generation is bumped, the findings untouched — so a fire-and-forget caller must observe the
    /// task. The gateway never throws for the expected failures, so this only concerns bugs.
    /// </remarks>
    /// <param name="key">The input's key.</param>
    /// <param name="source">The expression as it stands.</param>
    /// <param name="check">
    /// Asks the check with the submit's token; <see langword="null"/> when it could not be asked, which clears the
    /// input's findings unless a newer submit owns it by then.
    /// </param>
    /// <returns>A task that ends when this submit is settled, answered or abandoned.</returns>
    public async Task SubmitAsync(
        string key, string source, Func<string, CancellationToken, Task<ManagementExpressionVerdict?>> check)
    {
        var input = Supersede(key);
        var token = input.Token.Token;

        if (string.IsNullOrWhiteSpace(source))
        {
            Store(input, _noFindings);
            return;
        }

        try
        {
            await Task.Delay(DebounceOverride ?? Debounce, token);
            var verdict = await check(source, token);
            if (!input.Token.IsCancellationRequested)
            {
                Store(input, verdict?.Findings ?? _noFindings);
            }
        }
        catch (OperationCanceledException) when (input.Token.IsCancellationRequested)
        {
            // Superseded by a newer submit: its answer is the one that counts.
        }
    }

    private Input Supersede(string key)
    {
        _inputs.TryGetValue(key, out var previous);
        previous?.Token.Cancel();
        previous?.Token.Dispose();

        return _inputs[key] = new Input(previous?.Findings ?? _noFindings);
    }

    private void Store(Input input, IReadOnlyList<DescriptorValidationError> findings)
    {
        input.Findings = findings;
        Changed?.Invoke();
    }

    /// <summary>One input's findings and the token of the submit that currently owns it.</summary>
    private sealed class Input(IReadOnlyList<DescriptorValidationError> findings)
    {
        public IReadOnlyList<DescriptorValidationError> Findings { get; set; } = findings;

        public CancellationTokenSource Token { get; } = new();
    }
}
