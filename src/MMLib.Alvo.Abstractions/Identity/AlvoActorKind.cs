namespace MMLib.Alvo;

/// <summary>
/// How the principal behind an <see cref="AlvoContext"/> authenticated — never what it may do. Each member maps onto
/// one value of the CloudEvents authcontext extension's <c>authtype</c>, which is how an event reports it.
/// </summary>
public enum AlvoActorKind
{
    /// <summary>Nothing says how the caller authenticated; reported as <c>unknown</c>. Only ever set explicitly.</summary>
    Unknown,

    /// <summary>The caller presented no credential; reported as <c>unauthenticated</c>.</summary>
    Anonymous,

    /// <summary>Alvo itself acted, as <see cref="AlvoContext.System"/>; reported as <c>system</c>.</summary>
    System,

    /// <summary>An API key authenticated the caller; reported as <c>api_key</c>.</summary>
    ApiKey,

    /// <summary>A signed-in person authenticated the caller; reported as <c>user</c>.</summary>
    User,
}
