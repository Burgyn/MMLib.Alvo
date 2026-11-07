using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Functions;

/// <summary>What one run of a function knows besides its input: the event, the attempt, data access and its steps.</summary>
public sealed class AlvoFunctionContext
{
    internal AlvoFunctionContext(string function, AlvoEvent @event, int attempt, IAlvoSession data, IAlvoSteps steps)
    {
        Function = function;
        Event = @event;
        Attempt = attempt;
        Data = data;
        Steps = steps;
    }

    /// <summary>
    /// A context for calling a function's <c>InvokeAsync</c> yourself — in a unit test, say; deliveries build their own.
    /// </summary>
    /// <remarks>
    /// <paramref name="data"/> is used as it is: writes through <see cref="Data"/> carry no causation from
    /// <paramref name="event"/>. Its <see cref="Steps"/> run every step's work every time and never sleep.
    /// </remarks>
    /// <param name="function">The function's registered name.</param>
    /// <param name="event">The event that triggered the run.</param>
    /// <param name="data">Data access as the function's caller, such as <c>IAlvoSessions.For(caller)</c>.</param>
    /// <param name="attempt">Which attempt this is, from 1.</param>
    /// <returns>The context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="event"/> or <paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="function"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> is less than 1.</exception>
    public static AlvoFunctionContext Create(string function, AlvoEvent @event, IAlvoSession data, int attempt = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        return new(function, @event, attempt, data, PassThroughSteps.Instance);
    }

    /// <summary>Gets the function's registered name.</summary>
    public string Function { get; }

    /// <summary>Gets the event that triggered the run, as it was stored.</summary>
    public AlvoEvent Event { get; }

    /// <summary>Gets the event's id — the same on every attempt, so the key to deduplicate on.</summary>
    public Guid EventId => Event.Id;

    /// <summary>Gets which attempt this is, starting at 1.</summary>
    public int Attempt { get; }

    /// <summary>
    /// Gets data access as <see cref="Caller"/>. Inside a delivery, writes made here carry the event as their cause, one hop
    /// deeper in the causation chain; a context from <see cref="Create"/> carries neither.
    /// </summary>
    public IAlvoSession Data { get; }

    /// <summary>Gets the caller the function acts as.</summary>
    public AlvoContext Caller => Data.Context;

    /// <summary>
    /// Gets the run's steps: work split into named steps, each run until it completes once, so a retry resumes after the last
    /// completed step. A context from <see cref="Create"/> runs every step's work every time and never sleeps.
    /// </summary>
    public IAlvoSteps Steps { get; }
}
