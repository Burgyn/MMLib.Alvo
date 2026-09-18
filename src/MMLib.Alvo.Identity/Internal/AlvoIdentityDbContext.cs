using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// ASP.NET Core Identity's own store, mapped onto the <c>alvo_identity_*</c> tables
/// <see cref="AlvoFrameworkTables"/> reserves.
/// </summary>
/// <remarks>
/// <b>The names are spelled from <see cref="AlvoFrameworkTables"/>, never repeated here.</b> That table
/// is what the schema introspector excludes from the user's schema and what the descriptor validator
/// refuses as an entity name; a name this context invented would be a table the next re-apply plans to
/// DROP, taking every operator account with it.
/// </remarks>
/// <param name="options">The context's configured options.</param>
internal sealed class AlvoIdentityDbContext(DbContextOptions<AlvoIdentityDbContext> options)
    : IdentityDbContext<AlvoIdentityUser, AlvoIdentityRole, Guid>(options)
{
    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        MapTables(builder, AlvoIdentityTables.Prefix);
    }

    /// <summary>Maps every identity entity onto its reserved table name.</summary>
    /// <param name="builder">The model builder.</param>
    /// <param name="prefix">The schema prefix the reserved names are built under.</param>
    private static void MapTables(ModelBuilder builder, string prefix)
    {
        builder.Entity<AlvoIdentityUser>().ToTable(prefix + AlvoFrameworkTables.IdentityUsersSuffix);
        builder.Entity<AlvoIdentityRole>().ToTable(prefix + AlvoFrameworkTables.IdentityRolesSuffix);
        builder.Entity<IdentityUserRole<Guid>>().ToTable(prefix + AlvoFrameworkTables.IdentityUserRolesSuffix);
        builder.Entity<IdentityUserClaim<Guid>>().ToTable(prefix + AlvoFrameworkTables.IdentityUserClaimsSuffix);
        builder.Entity<IdentityUserLogin<Guid>>().ToTable(prefix + AlvoFrameworkTables.IdentityUserLoginsSuffix);
        builder.Entity<IdentityUserToken<Guid>>().ToTable(prefix + AlvoFrameworkTables.IdentityUserTokensSuffix);
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable(prefix + AlvoFrameworkTables.IdentityRoleClaimsSuffix);
    }
}

/// <summary>
/// The schema prefix the identity tables are built under.
/// </summary>
/// <remarks>
/// A static rather than an option read through DI because <see cref="DbContext.OnModelCreating"/> feeds
/// EF's model cache, which is keyed on the context type alone: a per-instance prefix would give two
/// hosts in one process one another's table names. <c>AlvoOptions.SchemaPrefix</c> is validated against
/// <c>^[a-z][a-z0-9_]{0,15}$</c>, so the value interpolated into DDL is always a validated identifier.
/// </remarks>
internal static class AlvoIdentityTables
{
    /// <summary>The one prefix the identity tables are created under.</summary>
    internal const string Prefix = "alvo";
}
