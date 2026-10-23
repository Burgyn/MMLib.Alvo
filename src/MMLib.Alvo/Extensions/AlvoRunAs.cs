namespace MMLib.Alvo.Extensions;

/// <summary>Who an event handler acts as when it reads and writes.</summary>
public enum AlvoRunAs
{
    /// <summary>The system, in the event's tenant — the built-in <c>admin</c> role, never another tenant.</summary>
    System = 0,

    /// <summary>
    /// The caller who made the change, as recorded on the event: its id, its tenant, and the names of the roles it held,
    /// resolved against the roles the descriptor declares now. A name no longer declared is dropped; with none left the
    /// handler acts as <c>anon</c>, and an event the system raised runs as <c>admin</c>.
    /// </summary>
    EventActor = 1,
}
