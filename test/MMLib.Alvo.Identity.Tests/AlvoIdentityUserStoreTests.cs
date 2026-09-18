using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// The default <see cref="IAlvoUserStore"/>, over a real SQLite file rather than a fake — the port's
/// contract is about persisted membership, and an in-memory dictionary cannot fail the way a store can.
/// </summary>
public sealed class AlvoIdentityUserStoreTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-identity-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;

    /// <summary>
    /// One scope for the whole class, because every service under test is scoped — the
    /// <c>DbContext</c>, <c>UserManager</c> and the store — and resolving a scoped service from the root
    /// provider throws.
    /// </summary>
    private IServiceProvider Services => _scope.ServiceProvider;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}"));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateScope();

        var db = Services.GetRequiredService<AlvoIdentityDbContext>();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    /// <summary>Both lookups find the same stored user, with the roles it was given.</summary>
    [Fact]
    public async Task A_created_user_is_found_by_id_and_by_email()
    {
        var id = await CreateAsync("eva@example.test", "editor");
        var store = Services.GetRequiredService<IAlvoUserStore>();

        var byId = await store.FindAsync(id, TestContext.Current.CancellationToken);
        var byEmail = await store.FindByEmailAsync("eva@example.test", TestContext.Current.CancellationToken);

        byId.ShouldNotBeNull().Email.ShouldBe("eva@example.test");
        byId.RoleNames.ShouldBe(["editor"]);
        byEmail.ShouldNotBeNull().Id.ShouldBe(id);
    }

    /// <summary>A sign-in address is matched however it was typed.</summary>
    [Fact]
    public async Task Email_lookup_ignores_case_because_a_sign_in_address_is_not_case_sensitive()
    {
        await CreateAsync("Eva@Example.test", "editor");
        var store = Services.GetRequiredService<IAlvoUserStore>();

        (await store.FindByEmailAsync("eva@example.TEST", TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    /// <summary>Setting roles is a replacement, so a revoked role really is revoked.</summary>
    [Fact]
    public async Task Setting_roles_replaces_rather_than_adds()
    {
        var id = await CreateAsync("eva@example.test", "editor", "auditor");
        var store = Services.GetRequiredService<IAlvoUserStore>();

        await store.SetRolesAsync(id, ["auditor"], TestContext.Current.CancellationToken);

        var user = await store.FindAsync(id, TestContext.Current.CancellationToken);
        user.ShouldNotBeNull().RoleNames.ShouldBe(["auditor"]);
    }

    /// <summary>The listing is the administration screen's source, so it holds everyone.</summary>
    [Fact]
    public async Task Listing_returns_every_stored_user()
    {
        await CreateAsync("eva@example.test", "editor");
        await CreateAsync("otto@example.test");
        var store = Services.GetRequiredService<IAlvoUserStore>();

        var users = await store.ListAsync(TestContext.Current.CancellationToken);

        users.Select(user => user.Email).OrderBy(email => email, StringComparer.Ordinal)
            .ShouldBe(["eva@example.test", "otto@example.test"]);
    }

    /// <summary>A miss is the port's documented <see langword="null"/>, never a throw.</summary>
    [Fact]
    public async Task An_unknown_user_is_null_rather_than_an_exception()
    {
        var store = Services.GetRequiredService<IAlvoUserStore>();

        (await store.FindAsync(UserId.New(), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.FindByEmailAsync("nobody@example.test", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    /// <summary>Creates a user through <c>UserManager</c>, with the named roles.</summary>
    /// <param name="email">The sign-in address.</param>
    /// <param name="roleNames">The roles to create and assign.</param>
    /// <returns>The new user's identifier.</returns>
    private async Task<UserId> CreateAsync(string email, params string[] roleNames)
    {
        var users = Services.GetRequiredService<UserManager<AlvoIdentityUser>>();
        var roles = Services.GetRequiredService<RoleManager<AlvoIdentityRole>>();
        var user = new AlvoIdentityUser { UserName = email, Email = email };

        (await users.CreateAsync(user, "Str0ng!Passw0rd")).Succeeded.ShouldBeTrue();
        foreach (var roleName in roleNames)
        {
            await roles.CreateAsync(new AlvoIdentityRole { Name = roleName });
        }

        (await users.AddToRolesAsync(user, roleNames)).Succeeded.ShouldBeTrue();
        return new UserId(user.Id);
    }
}
