namespace MMLib.Alvo.Events;

/// <summary>Who drives Alvo's event dispatchers: their own background loops, or the host, one round at a time.</summary>
public enum AlvoEventDispatchMode
{
    /// <summary>The dispatchers poll on their own background loops. The default.</summary>
    Background = 0,

    /// <summary>
    /// The dispatchers never poll; each call to <c>IAlvoEventPump.DrainAsync</c> runs one round. For tests and tools that
    /// must decide exactly when events are delivered — a standalone host refuses it.
    /// </summary>
    Manual = 1,
}
