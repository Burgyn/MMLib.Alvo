using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// The bootstrap administrator: created from infrastructure configuration, once, and identifiable
/// afterwards so #146 can let it past the descriptor's <c>access</c> block.
/// </summary>
public sealed class AlvoIdentityBootstrapTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-bootstrap-{Guid.NewGuid():N}.db");
    private readonly string _passwordFile = Path.Combine(Path.GetTempPath(), $"alvo-pw-{Guid.NewGuid():N}.txt");

    /// <inheritdoc/>
    public ValueTask InitializeAsync()
    {
        File.WriteAllText(_passwordFile, "Str0ng!Passw0rd\n");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_passwordFile);
        File.Delete(_file);
        return ValueTask.CompletedTask;
    }

    /// <summary>The configured administrator exists after a start, and holds the built-in admin role.</summary>
    [Fact]
    public async Task A_configured_bootstrap_admin_is_created_and_holds_the_admin_role()
    {
        await using var host = Host(bootstrapEmail: "admin@example.test");
        await Start(host);

        var user = await UsersOf(host)
            .FindByEmailAsync("admin@example.test", TestContext.Current.CancellationToken);

        user.ShouldNotBeNull().RoleNames.ShouldContain(MMLib.Alvo.Role.Admin.Name);
    }

    /// <summary>
    /// The seeded account is the one the port names — and the reserved all-zero identifier, which means
    /// "no identity", never is.
    /// </summary>
    [Fact]
    public async Task The_created_admin_is_the_one_the_bootstrap_port_names()
    {
        await using var host = Host(bootstrapEmail: "admin@example.test");
        await Start(host);

        var user = await UsersOf(host)
            .FindByEmailAsync("admin@example.test", TestContext.Current.CancellationToken);

        var bootstrap = host.GetRequiredService<IAlvoBootstrapAdmin>();
        bootstrap.IsBootstrapAdmin(user.ShouldNotBeNull().Id).ShouldBeTrue();
        bootstrap.IsBootstrapAdmin(UserId.New()).ShouldBeFalse();
        bootstrap.IsBootstrapAdmin(default).ShouldBeFalse();
    }

    /// <summary>
    /// Restarting is the normal case — a container restarts on every deploy — so seeding must be a
    /// no-op the second time rather than a duplicate account or a failed start.
    /// </summary>
    /// <remarks>
    /// The secret file is rotated between the two starts, and the account must still authenticate with
    /// the <em>original</em> password: re-seeding a live administrator's credential from a file would
    /// make rotating that file a silent password reset, which is a dashboard operation, not a restart's.
    /// </remarks>
    [Fact]
    public async Task Seeding_twice_creates_one_account_and_does_not_reset_its_password()
    {
        await using (var first = Host(bootstrapEmail: "admin@example.test"))
        {
            await Start(first);
        }

        File.WriteAllText(_passwordFile, "AnotherStr0ng!Passw0rd\n");

        await using var second = Host(bootstrapEmail: "admin@example.test");
        await Start(second);

        var users = await UsersOf(second).ListAsync(TestContext.Current.CancellationToken);

        users.Count(user => user.Email == "admin@example.test").ShouldBe(1);
        (await PasswordHoldsAsync(second, "Str0ng!Passw0rd")).ShouldBeTrue("the restart must not reset it");
        (await PasswordHoldsAsync(second, "AnotherStr0ng!Passw0rd")).ShouldBeFalse("rotation is not a restart");
    }

    /// <summary>The image ships no credential: without configuration, nothing is created and nobody is named.</summary>
    [Fact]
    public async Task No_configured_email_creates_no_account_and_names_no_bootstrap_admin()
    {
        await using var host = Host(bootstrapEmail: null);
        await Start(host);

        (await UsersOf(host).ListAsync(TestContext.Current.CancellationToken))
            .ShouldBeEmpty("the image ships no credential; the deployment configures one");
        host.GetRequiredService<IAlvoBootstrapAdmin>().IsBootstrapAdmin(UserId.New()).ShouldBeFalse();
    }

    /// <summary>Runs the registered bootstrap, exactly as a host's start would.</summary>
    /// <param name="host">The built container.</param>
    /// <returns>A task that completes when the bootstrap has run.</returns>
    private static Task Start(ServiceProvider host) =>
        host.GetServices<IHostedService>().OfType<AlvoIdentityBootstrap>().Single()
            .StartAsync(TestContext.Current.CancellationToken);

    /// <summary>Asks ASP.NET Core Identity whether the stored administrator still answers to a password.</summary>
    /// <param name="host">The built container.</param>
    /// <param name="password">The password to check.</param>
    /// <returns><see langword="true"/> when the stored hash matches.</returns>
    private static async Task<bool> PasswordHoldsAsync(ServiceProvider host, string password)
    {
        using var scope = host.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AlvoIdentityUser>>();
        var stored = await users.FindByEmailAsync("admin@example.test");

        return await users.CheckPasswordAsync(stored.ShouldNotBeNull(), password);
    }

    /// <summary>
    /// The store, resolved through a scope. It is scoped because its <c>DbContext</c> is, so resolving it
    /// from the root provider throws — and the scope is deliberately created per read rather than held,
    /// so each fact reads through a fresh <c>DbContext</c> and cannot pass on a change tracker's memory
    /// of a write the database never took.
    /// </summary>
    /// <param name="host">The built container.</param>
    /// <returns>The user store.</returns>
    private static IAlvoUserStore UsersOf(ServiceProvider host) =>
        host.CreateScope().ServiceProvider.GetRequiredService<IAlvoUserStore>();

    /// <summary>Builds a container over one SQLite file, configured as a deployment would be.</summary>
    /// <param name="bootstrapEmail">The configured administrator, or <see langword="null"/> for none.</param>
    /// <returns>The built container.</returns>
    private ServiceProvider Host(string? bootstrapEmail)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(
            store => store.UseSqlite($"Data Source={_file}"),
            identity =>
            {
                identity.BootstrapEmail = bootstrapEmail;
                identity.BootstrapPasswordFile = bootstrapEmail is null ? null : _passwordFile;
            });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
