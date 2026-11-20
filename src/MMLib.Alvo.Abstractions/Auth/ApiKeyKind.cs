namespace MMLib.Alvo.Auth;

/// <summary>What kind of credential an <see cref="ApiKeyRecord"/> is, and so whom it acts as.</summary>
public enum ApiKeyKind
{
    /// <summary>A key the host or operator configured (a dev key, or one a host's own store issues); today's meaning of every key.</summary>
    Configured = 0,

    /// <summary>A personal token a signed-in person minted; it acts as that person, with their tenant roles read live.</summary>
    Personal = 1,

    /// <summary>A service token a tenant owner minted; it acts as the tenant, with the fixed tenant roles it was given.</summary>
    Service = 2,
}
