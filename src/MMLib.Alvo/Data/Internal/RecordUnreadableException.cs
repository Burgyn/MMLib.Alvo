namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// A record a host type cannot be built from: a mapped field missing, null where the property cannot hold null, a value
/// the property cannot take, or a type no record maps. Its message names the type and the field, never a value, so it is
/// safe to log; a delivery whose typed message throws it is dead at once, since the same record fails again.
/// </summary>
internal sealed class RecordUnreadableException(string message) : InvalidOperationException(message);
