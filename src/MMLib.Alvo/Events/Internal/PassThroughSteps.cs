namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The steps outside a delivery — a context built for a unit test, a delivered fixture: every step runs its work every time,
/// and a sleep returns at once.
/// </summary>
internal sealed class PassThroughSteps : IAlvoSteps
{
    internal static PassThroughSteps Instance { get; } = new();

    private PassThroughSteps()
    {
    }

    public Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        StepNames.Check(name);
        ArgumentNullException.ThrowIfNull(work);

        return work(cancellationToken);
    }

    public Task RunAsync(string name, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        StepNames.Check(name);
        ArgumentNullException.ThrowIfNull(work);

        return work(cancellationToken);
    }

    public Task SleepUntilAsync(string name, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        StepNames.Check(name);

        return Task.CompletedTask;
    }
}
