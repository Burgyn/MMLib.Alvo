namespace MMLib.Alvo.Extensions;

/// <summary>Whether a handler sees one record's events strictly in order.</summary>
public enum AlvoOrdering
{
    /// <summary>
    /// One record's events reach the handler in order: a delivery that is dead holds the record's later ones back until it
    /// is no longer dead. A dead delivery kept for an event missed while the handler could not be bound holds nothing back.
    /// </summary>
    PerRecord = 0,

    /// <summary>Every delivery runs as soon as it is due, whatever happened to the record's earlier ones.</summary>
    None = 1,
}
