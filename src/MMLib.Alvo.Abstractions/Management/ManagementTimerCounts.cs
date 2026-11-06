namespace MMLib.Alvo.Management;

/// <summary>How many timers this instance holds armed, and how many of them are due now.</summary>
public sealed record ManagementTimerCounts
{
    /// <summary>Gets how many timers are armed, due or not.</summary>
    public required int Pending { get; init; }

    /// <summary>Gets how many armed timers are already due — a number that stays above zero means nothing is firing them.</summary>
    public required int Due { get; init; }
}
