namespace MMLib.Alvo.Extensions;

/// <summary>What happens, at start-up, to the unfinished work of an extension that is no longer registered.</summary>
public enum AlvoRetirement
{
    /// <summary>Its deliveries become dead with the reason <c>retired</c>, so they stay inspectable.</summary>
    DeadLetter = 0,

    /// <summary>Its deliveries are cancelled.</summary>
    Discard = 1,
}
