namespace MMLib.Alvo.Events;

/// <summary>
/// Named steps inside one delivery: each step's work runs until it completes once, and a retry of the delivery returns the
/// stored result of every step that already completed instead of running it again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each step is at-least-once, so its work must be idempotent.</b> The result is recorded after the work returns; a crash
/// between the two runs that one step again. A step that completed is never run again for the same event and handler.
/// </para>
/// <para>
/// A step's name is 1–200 characters with no control characters, and unique within one run of the handler; a delivery has
/// at most 1 000 steps, and a step's result is at most 256 KiB of JSON (<c>System.Text.Json</c>, web defaults). Outside a
/// delivery — a context built for a unit test, a delivered fixture — every step runs its work every time and a sleep returns
/// at once.
/// </para>
/// </remarks>
public interface IAlvoSteps
{
    /// <summary>Runs the step's work unless it already completed in this delivery, and returns its result.</summary>
    /// <typeparam name="T">The result's type, serialized as JSON.</typeparam>
    /// <param name="name">The step's name, unique within one run of the handler.</param>
    /// <param name="work">The step's work, given the handler's token.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result the step first completed with.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, longer than 200 characters or holds a control character.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="name"/> was already used in this run, the run has used 1 000 steps, or the result is over 256 KiB.
    /// </exception>
    Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>Runs the step's work unless it already completed in this delivery.</summary>
    /// <param name="name">The step's name, unique within one run of the handler.</param>
    /// <param name="work">The step's work, given the handler's token.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the step has completed, now or before.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, longer than 200 characters or holds a control character.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="name"/> was already used in this run, or the run has used 1 000 steps.</exception>
    Task RunAsync(string name, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the delivery's attempt until <paramref name="at"/>, when the delivery runs again and this call returns; an
    /// instant already passed returns at once. The wait spends no attempt.
    /// </summary>
    /// <param name="name">The sleep's step name, unique within one run of the handler.</param>
    /// <param name="at">When the delivery resumes.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes once <paramref name="at"/> has passed.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, longer than 200 characters or holds a control character.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="name"/> was already used in this run, or the run has used 1 000 steps.</exception>
    Task SleepUntilAsync(string name, DateTimeOffset at, CancellationToken cancellationToken = default);
}
