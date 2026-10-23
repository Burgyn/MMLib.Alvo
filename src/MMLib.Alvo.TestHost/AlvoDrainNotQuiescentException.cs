namespace MMLib.Alvo.TestHost;

/// <summary>
/// <see cref="AlvoTestHost.DrainEventsAsync"/> gave up: the event queue was still producing work after its round limit,
/// which in a test almost always means a subscriber writes to what it subscribes to.
/// </summary>
public sealed class AlvoDrainNotQuiescentException : Exception
{
    private const string DefaultMessage = "The event queue did not go quiet within the drain's round limit.";

    /// <summary>Initializes a new instance with the default message.</summary>
    public AlvoDrainNotQuiescentException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
    /// <param name="message">The message.</param>
    public AlvoDrainNotQuiescentException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/> and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoDrainNotQuiescentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private AlvoDrainNotQuiescentException(int rounds, IReadOnlyList<string> subscribers)
        : base(Describe(rounds, subscribers))
    {
        Rounds = rounds;
        Subscribers = subscribers;
    }

    /// <summary>Gets how many rounds ran before the drain gave up; <c>0</c> on an instance built by hand.</summary>
    public int Rounds { get; }

    /// <summary>Gets the subscribers still running in the last rounds — the likely loop.</summary>
    public IReadOnlyList<string> Subscribers { get; } = [];

    /// <summary>The refusal after <paramref name="rounds"/> rounds, naming <paramref name="subscribers"/>.</summary>
    internal static AlvoDrainNotQuiescentException After(int rounds, IReadOnlyList<string> subscribers) =>
        new(rounds, subscribers);

    private static string Describe(int rounds, IReadOnlyList<string> subscribers) =>
        $"The event queue was still busy after {rounds} drain rounds, so the drain gave up instead of running forever. "
        + $"Still running at the end: {string.Join(", ", subscribers)}. A subscriber that writes to the entity it "
        + "subscribes to — a webhook receiver writing back, for example — produces a new event on every delivery; give "
        + "it a condition its own writes do not match.";
}
