namespace MMLib.Alvo.Schema;

/// <summary>Enumeration of storage modes for entities.</summary>
public enum EntityStorage
{
    /// <summary>Physical storage (mapped to database tables).</summary>
    Physical,

    /// <summary>Dynamic storage (metadata-driven, partitioned).</summary>
    Dynamic
}
