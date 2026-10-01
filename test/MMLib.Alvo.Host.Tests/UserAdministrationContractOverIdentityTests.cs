using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Testing.Management;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The one <see cref="IAlvoUserAdministration"/> that exists, run against the contract every
/// implementation must satisfy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved from a composed container, deliberately.</b> §6.1's fact is not "the guard class
/// works" — a unit test over <c>GuardedUserAdministration</c> would prove that and prove nothing
/// about the arrangement. It is that <em>resolving the public interface gets the guard</em>: there
/// must be no registration anywhere that hands an in-process caller the raw implementation. So the
/// suite asks the host's own container, exactly as the dashboard does.
/// </para>
/// <para>
/// The caller is published on <see cref="IAlvoContextAccessor"/> rather than passed, because that
/// is how every management call carries its caller — the route filter publishes it, and the
/// dashboard's gateway publishes it. A test that passed a caller as an argument would be measuring
/// a contract this port does not have.
/// </para>
/// </remarks>
public sealed class UserAdministrationContractOverIdentityTests
    : UserAdministrationContractTests, IAsyncLifetime
{
    private AlvoHostWorld? _world;
    private IServiceScope? _scope;

    /// <inheritdoc/>
    protected override UserId Administrator => _administrator;

    /// <inheritdoc/>
    protected override UserId BootstrapAdministrator => _bootstrap;

    private UserId _bootstrap;
    private UserId _administrator;
    private string _secret = string.Empty;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        /* A bootstrap administrator has to exist, because two of the facts are about refusing
           writes aimed at it — without one they would measure "no such user" instead. */
        _secret = Path.Combine(Path.GetTempPath(), $"alvo-bootstrap-{Guid.CreateVersion7():N}.txt");
        await File.WriteAllTextAsync(_secret, "Str0ng!Passw0rd", TestContext.Current.CancellationToken);

        _world = await AlvoHostWorld.StartAsync(
            Descriptor,
            overrides: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Alvo:Admin:BootstrapEmail"] = "bootstrap@alvo.test",
                ["Alvo:Admin:BootstrapPasswordFile"] = _secret,
            });
        _scope = _world.Services.CreateScope();

        var bootstrap = _scope.ServiceProvider.GetRequiredService<IAlvoBootstrapAdmin>();
        var store = _scope.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        var everybody = await store.ListAsync(TestContext.Current.CancellationToken);

        _bootstrap = everybody.Select(person => person.Id)
            .FirstOrDefault(id => bootstrap.IsBootstrapAdmin(id));

        _bootstrap.ShouldNotBe(
            default,
            "without a seeded bootstrap administrator the two refusal facts would measure "
            + "\"no such user\" and pass for the wrong reason");

        /* A real account for the administrator the facts act as: three of them write to that row,
           and a made-up id would fail on "no such user" rather than on the guard. Created through
           the unguarded implementation on purpose — the arrangement under test is what a caller
           gets from the PUBLIC interface, and seeding through it would need a caller to already
           exist. */
        var unguarded = _scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey);

        var created = await unguarded.CreateAsync(
            new AlvoUserCreation("operator@alvo.test", ["operator"]),
            TestContext.Current.CancellationToken);

        _administrator = created.Id;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _scope?.Dispose();
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }

        if (_secret.Length > 0)
        {
            File.Delete(_secret);
        }
    }

    /// <inheritdoc/>
    protected override IAlvoUserAdministration AsUnprivilegedCaller()
        => Publishing(new AlvoContext
        {
            User = new UserId(Guid.CreateVersion7()),

            /* Anon rather than an empty set: a caller always holds at least one role, and
               AlvoContext refuses an empty one by design — an empty set would be a caller the type
               says cannot exist. */
            Roles = new HashSet<Role> { Role.Anon },
        });

    /// <inheritdoc/>
    protected override IAlvoUserAdministration AsAdministrator()
        => Publishing(new AlvoContext
        {
            User = Administrator,
            Roles = new HashSet<Role> { RoleCatalog.Create(["operator"]).Get("operator") },
        });

    private IAlvoUserAdministration Publishing(AlvoContext caller)
    {
        var services = _scope!.ServiceProvider;
        services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = caller,
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = $"test:{caller.User}",
        };

        return services.GetRequiredService<IAlvoUserAdministration>();
    }

    /// <summary>
    /// A descriptor whose <c>access.admin</c> is decided by a role that is not called <c>admin</c>.
    /// </summary>
    private const string Descriptor = "host-user-admin.alvo.json";
}
