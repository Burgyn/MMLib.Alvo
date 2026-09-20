using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// The identity tables follow <see cref="AlvoOptions.SchemaPrefix"/>, the option both the introspector's
/// exclusion set and the validator's reserved-name set are built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>A prefix that only half the framework honours is the #156 failure again.</b> A host calling
/// <c>UseSchemaPrefix("acme")</c> reserves <c>acme_identity_*</c>; a context still creating
/// <c>alvo_identity_*</c> puts every operator account inside the <em>user's</em> schema, where the differ
/// plans a <c>DROP</c> on the next re-apply and a <c>developer</c> may declare an entity over the users
/// table and serve read/write over password hashes.
/// </para>
/// <para>
/// <b><see cref="Two_prefixes_in_one_process_get_two_models"/> is the fact that fails a naive fix.</b> EF
/// caches a built model under a key that is the context type alone, in a cache that lives in EF's
/// process-wide internal service provider — so simply reading the option in <c>OnModelCreating</c> makes
/// the second container silently reuse the first one's tables. Only the model-cache-key half closes it.
/// </para>
/// </remarks>
public class AlvoIdentitySchemaPrefixTests
{
    [Fact]
    public void The_identity_tables_are_named_under_the_configured_prefix()
    {
        using var provider = Container("acme");
        using var scope = provider.CreateScope();

        TableNamesOf(scope.ServiceProvider).ShouldAllBe(name => name.StartsWith("acme", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tables the context maps are exactly the identity tables <see cref="AlvoFrameworkTables"/>
    /// reserves, under a non-default prefix.
    /// </summary>
    /// <remarks>
    /// <c>FrameworkTableReservationTests</c> pins that <c>NamesFor</c> follows the prefix, and nothing pinned
    /// that this package uses the same value — which is exactly how the constant survived.
    /// </remarks>
    [Fact]
    public void Every_mapped_table_is_a_name_the_framework_reserves_under_that_prefix()
    {
        using var provider = Container("acme");
        using var scope = provider.CreateScope();

        var reserved = AlvoFrameworkTables.NamesFor("acme");
        var mapped = TableNamesOf(scope.ServiceProvider);

        mapped.Count.ShouldBe(AlvoFrameworkTables.IdentitySuffixes.Count);
        mapped.ShouldBeSubsetOf(reserved);
    }

    /// <summary>
    /// Two containers under two prefixes, in one process, map two different sets of tables.
    /// </summary>
    /// <remarks>
    /// <b>This is the fact a field swap passes only by luck of ordering.</b> EF's model cache is keyed on the
    /// context type and held in an internal service provider shared by every identically-configured
    /// container, so without <see cref="AlvoIdentityModelCacheKeyFactory"/> the second container here is
    /// served the first one's model and both answer <c>alvo_identity_users</c>.
    /// </remarks>
    [Fact]
    public void Two_prefixes_in_one_process_get_two_models()
    {
        using var first = Container("alvo");
        using var second = Container("acme");
        using var firstScope = first.CreateScope();
        using var secondScope = second.CreateScope();

        TableNamesOf(firstScope.ServiceProvider).ShouldContain("alvo" + AlvoFrameworkTables.IdentityUsersSuffix);
        TableNamesOf(secondScope.ServiceProvider).ShouldContain("acme" + AlvoFrameworkTables.IdentityUsersSuffix);
    }

    /// <summary>The table names the context's model maps, in no significant order.</summary>
    /// <param name="scope">The scope the context is resolved from.</param>
    /// <returns>The mapped table names.</returns>
    private static IReadOnlyCollection<string> TableNamesOf(IServiceProvider scope)
    {
        var store = scope.GetRequiredService<AlvoIdentityDbContext>();

        return [.. store.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A container with the identity package installed under one schema prefix.</summary>
    /// <param name="schemaPrefix">The prefix the host configured.</param>
    /// <returns>The built container.</returns>
    private static ServiceProvider Container(string schemaPrefix)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AlvoOptions>(options => options.SchemaPrefix = schemaPrefix);
        services.AddAlvoIdentity(store => store.UseSqlite("Data Source=:memory:"));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
