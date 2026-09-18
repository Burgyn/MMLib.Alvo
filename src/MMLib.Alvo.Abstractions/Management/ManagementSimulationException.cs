namespace MMLib.Alvo.Management;

/// <summary>A simulation names something the framework cannot resolve — an operation, a role, a caller.</summary>
/// <remarks>
/// <b>The message is what reaches the caller as the 422's <c>detail</c></b>, so every construction of it
/// names what was sent <em>and</em> what is accepted. A refusal that only said "invalid" would send an agent
/// to the descriptor for something this endpoint already knows.
/// </remarks>
public sealed class ManagementSimulationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementSimulationException"/> class.</summary>
    public ManagementSimulationException()
        : base("The simulation names something the framework cannot resolve.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementSimulationException"/> class.</summary>
    /// <param name="message">The message, naming what was sent and what is accepted.</param>
    public ManagementSimulationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementSimulationException"/> class.</summary>
    /// <param name="message">The message, naming what was sent and what is accepted.</param>
    /// <param name="innerException">The refusal this one restates.</param>
    public ManagementSimulationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
