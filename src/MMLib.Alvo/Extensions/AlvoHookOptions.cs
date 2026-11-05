namespace MMLib.Alvo.Extensions;

/// <summary>How one write hook runs.</summary>
public sealed class AlvoHookOptions
{
    internal const int DefaultMaxOperations = 50;

    internal const int MaxMaxOperations = 1000;

    private int _order;

    /// <summary>Gets or sets what the hook does, in a sentence, for the dashboard and the docs.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets where the hook runs among the host's hooks of the same entity and stage: lower first; at one order an
    /// entity's own hooks before every-entity ones, then registration order. Declarative hooks always run first.
    /// </summary>
    public int Order
    {
        get => _order;
        set
        {
            _order = value;
            OrderStated = true;
        }
    }

    /// <summary>
    /// Gets or sets how long one run may take before its calls are cancelled and the write fails; <see langword="null"/> for
    /// the stage's default — 250 milliseconds inside the transaction, 5 seconds for a validate hook. At most 30 seconds inside
    /// the transaction and 60 seconds for validate; a longer or non-positive timeout refuses the start.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets how many calls one run may make through the write's transaction, reads included; 50 unless set, 1 to 1000.
    /// The next call throws <c>AlvoBudgetExceededException</c> and fails the write.
    /// </summary>
    public int MaxOperations { get; set; } = DefaultMaxOperations;

    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMilliseconds(250);

    internal static TimeSpan DefaultValidateTimeout { get; } = TimeSpan.FromSeconds(5);

    internal static TimeSpan MaxTimeout { get; } = TimeSpan.FromSeconds(30);

    internal static TimeSpan MaxValidateTimeout { get; } = TimeSpan.FromSeconds(60);

    internal bool OrderStated { get; private set; }
}
