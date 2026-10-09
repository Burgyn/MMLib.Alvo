using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The bike-workshop example with a watch on what the dashboard asks the management surface: every expression verdict,
/// by its source, and every function-list request. A scenario waits for exactly what it asserts (spec D-10) — the verdict
/// on the text it typed, or proof the list was asked for before it asserts that no list is drawn.
/// </summary>
public class RecordingWorld : AdminWorld
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ManagementExpressionVerdict>> _checks = new(StringComparer.Ordinal);

    private int _functionLists;

    /// <summary>How many function-list requests have finished, answered or refused.</summary>
    internal int FunctionListsAnswered => Volatile.Read(ref _functionLists);

    /// <inheritdoc/>
    protected override string Descriptor => Descriptors.BikeWorkshop;

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services) =>
        ManagementDecorator.Around(services, shipped => new Watching(shipped, this));

    /// <summary>The first verdict the dashboard was given for <paramref name="source"/>, waiting up to a minute for it.</summary>
    /// <remarks>
    /// A minute, the browser context's own timeout, rather than the ten seconds this waited: one CI run failed here with
    /// every other wait in the suite allowed six times as long. And it says which sources the dashboard did ask about, so
    /// a check that was never asked (a lost keystroke) is told apart from one asked about other text (a box reset).
    /// </remarks>
    /// <param name="source">The exact expression text.</param>
    /// <returns>The verdict.</returns>
    public async Task<ManagementExpressionVerdict> CheckedAsync(string source)
    {
        try
        {
            return await Check(source).Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException timeout)
        {
            var asked = _checks.Where(check => check.Value.Task.IsCompleted).Select(check => $"'{check.Key}'");
            throw new TimeoutException(
                $"The dashboard never checked '{source}'. It checked: {string.Join(", ", asked)}.", timeout);
        }
    }

    /// <summary>Waits until more than <paramref name="seen"/> function-list requests have finished.</summary>
    /// <param name="seen">The count read before the step that should ask.</param>
    /// <returns>A task that completes once the request has finished.</returns>
    public async Task FunctionListAnsweredAsync(int seen)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (FunctionListsAnswered <= seen)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the dashboard never asked for the function list");
            await Task.Delay(50);
        }
    }

    /// <summary>What this world answers for <c>cel/functions</c>; a world may refuse it instead.</summary>
    /// <param name="shipped">The shipped management's answer.</param>
    /// <returns>The answer the dashboard is given.</returns>
    protected virtual Task<ManagementCelFunctions> ListFunctionsAsync(Func<Task<ManagementCelFunctions>> shipped) => shipped();

    private TaskCompletionSource<ManagementExpressionVerdict> Check(string source) =>
        _checks.GetOrAdd(source, _ => new TaskCompletionSource<ManagementExpressionVerdict>(TaskCreationOptions.RunContinuationsAsynchronously));

    private sealed class Watching(IAlvoManagement inner, RecordingWorld world) : ManagementDecorator(inner)
    {
        public override async Task<ManagementExpressionVerdict> CheckExpressionAsync(
            string project, ManagementExpressionCheck request, CancellationToken ct = default)
        {
            var verdict = await base.CheckExpressionAsync(project, request, ct);
            world.Check(request.Source).TrySetResult(verdict);
            return verdict;
        }

        public override async Task<ManagementCelFunctions> GetCelFunctionsAsync(string project, CancellationToken ct = default)
        {
            try
            {
                return await world.ListFunctionsAsync(() => base.GetCelFunctionsAsync(project, ct));
            }
            finally
            {
                Interlocked.Increment(ref world._functionLists);
            }
        }
    }
}
