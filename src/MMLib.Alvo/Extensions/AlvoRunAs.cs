namespace MMLib.Alvo.Extensions;

/// <summary>Who an event handler acts as when it reads and writes.</summary>
public enum AlvoRunAs
{
    /// <summary>The system, in the event's tenant — the built-in <c>admin</c> role, never another tenant.</summary>
    System = 0,

    /// <summary>The caller who made the change, as recorded on the event: its id, tenant and the roles it held then.</summary>
    EventActor = 1,
}
