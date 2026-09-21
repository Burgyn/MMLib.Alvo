using Microsoft.AspNetCore.Identity;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>An administrator account, keyed by <see cref="Guid"/> so it maps straight onto <see cref="UserId"/>.</summary>
internal sealed class AlvoIdentityUser : IdentityUser<Guid>;

/// <summary>An identity role row. The descriptor still governs which of these names a rule may name.</summary>
internal sealed class AlvoIdentityRole : IdentityRole<Guid>;
