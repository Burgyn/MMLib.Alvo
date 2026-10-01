using Microsoft.AspNetCore.Identity;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>An administrator account, keyed by <see cref="Guid"/> so it maps straight onto <see cref="UserId"/>.</summary>
internal sealed class AlvoIdentityUser : IdentityUser<Guid>
{
    /// <summary>The one tenant this operator acts in, when an administrator has granted one.</summary>
    /// <remarks>
    /// Nullable, and a column rather than a claim: a claim lives in a cookie and would survive an
    /// administrator revoking the grant until the operator happened to sign out. The claim is
    /// minted from this column at sign-in, which makes the column the single fact.
    /// </remarks>
    public Guid? TenantId { get; set; }
}

/// <summary>An identity role row. The descriptor still governs which of these names a rule may name.</summary>
internal sealed class AlvoIdentityRole : IdentityRole<Guid>;
