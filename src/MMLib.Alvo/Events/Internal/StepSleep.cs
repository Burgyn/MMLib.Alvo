namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The signal <see cref="DeliverySteps.SleepUntilAsync"/> throws to end the attempt; the runner answers the sleep from the
/// steps' own flag, so a handler that catches this still sleeps.
/// </summary>
internal sealed class StepSleep : Exception
{
    public StepSleep()
        : base("The delivery sleeps until its wake time; this attempt ends here.")
    {
    }

    public StepSleep(string message)
        : base(message)
    {
    }

    public StepSleep(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
