using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// ASP.NET Core Identity's own store, mapped onto the <c>&lt;prefix&gt;_identity_*</c> tables
/// <see cref="AlvoFrameworkTables"/> reserves.
/// </summary>
/// <remarks>
/// <para>
/// <b>The names are spelled from <see cref="AlvoFrameworkTables"/>, never repeated here.</b> That table
/// is what the schema introspector excludes from the user's schema and what the descriptor validator
/// refuses as an entity name; a name this context invented would be a table the next re-apply plans to
/// DROP, taking every operator account with it.
/// </para>
/// <para>
/// <b>The prefix is <see cref="AlvoOptions.SchemaPrefix"/>, not a constant.</b> Both the introspector's
/// exclusion set and the validator's reserved-name set are built from that option, so a host calling
/// <c>UseSchemaPrefix("acme")</c> reserves <c>acme_identity_*</c> — and a context that still created
/// <c>alvo_identity_*</c> would put every operator account back inside the user's schema, where the
/// differ plans a <c>DROP</c> and a <c>developer</c> may declare an entity over it.
/// </para>
/// </remarks>
/// <param name="options">The context's configured options.</param>
/// <param name="alvo">Supplies the validated <see cref="AlvoOptions.SchemaPrefix"/> the tables are named under.</param>
internal sealed class AlvoIdentityDbContext(
    DbContextOptions<AlvoIdentityDbContext> options,
    IOptions<AlvoOptions> alvo)
    : IdentityDbContext<AlvoIdentityUser, AlvoIdentityRole, Guid>(options)
{
    /// <summary>
    /// The schema prefix this context's tables are named under.
    /// </summary>
    /// <remarks>
    /// <b>Read by <see cref="AlvoIdentityModelCacheKeyFactory"/> as well as by
    /// <see cref="OnModelCreating"/>.</b> EF caches a built model per cache key, and the default key is the
    /// context type alone — so two hosts in one process under two prefixes would share whichever model was
    /// built first. The prefix is part of the key precisely because it is part of the model.
    /// </remarks>
    internal string SchemaPrefix { get; } = alvo.Value.SchemaPrefix;

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        MapTables(builder, SchemaPrefix);
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
