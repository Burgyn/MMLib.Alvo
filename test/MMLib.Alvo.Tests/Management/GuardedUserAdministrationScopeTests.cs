using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Auth;
using NSubstitute;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// <b>Every administration call is its own unit of work.</b> The guarded decorator reaches the
/// implementation through a scope it creates for that one call, never through the scope it was itself
/// resolved from.
/// </summary>
/// <remarks>
/// <para>
/// In the dashboard the scope a service is resolved from is the Blazor circuit, open for as long as the
/// tab. An implementation resolved from it keeps one <c>DbContext</c> — and one change tracker — for the
/// whole of that time, so a single refused write left its rows tracked and every later write from the
/// tab re-flushed them and failed: an administrator who lost one race could then disable nobody until
/// they reloaded. A scope per call makes that impossible for <em>any</em> implementation, including a
/// host's own that caches in a scoped service, which a rule inside one adapter could never promise.
/// </para>
/// <para>
/// A recording implementation rather than the identity package: the fact is about the arrangement the
/// core makes, so it must hold for an implementation that does nothing about its own lifetime.
/// </para>
/// </remarks>
public sealed class GuardedUserAdministrationScopeTests
{
    private static readonly UserId _bootstrap = UserId.New();

    [Fact]
    public async Task Each_call_reaches_the_implementation_in_a_scope_of_its_own_that_ends_with_the_call()
    {
        var calls = new List<ScopeTag>();
        var ended = new List<ScopeTag>();
        using var provider = Container(calls, ended);
        using var circuit = provider.CreateScope();
        var tab = AsBootstrap(circuit);
        var target = UserId.New();

        await tab.SetDisabledAsync(target, disabled: true, TestContext.Current.CancellationToken);
        await tab.SetTenantAsync(target, TenantId.New(), TestContext.Current.CancellationToken);
        await tab.ListAsync(new AlvoUserQuery(), TestContext.Current.CancellationToken);
        await tab.ClearLockoutAsync(target, TestContext.Current.CancellationToken);

        var circuitsOwn = circuit.ServiceProvider.GetRequiredService<ScopeTag>();
        calls.Count.ShouldBe(4);
        calls.ShouldNotContain(circuitsOwn, "the circuit's scope must never hold the implementation's state");
        calls.Distinct().Count().ShouldBe(4, "one scope per call, not one per decorator");
        ended.ShouldBe(calls, "each call's scope is disposed when the call returns");
    }

    [Fact]
    public async Task A_call_that_throws_still_ends_its_scope()
    {
        var calls = new List<ScopeTag>();
        var ended = new List<ScopeTag>();
        using var provider = Container(calls, ended, fail: true);
        using var circuit = provider.CreateScope();

        await Should.ThrowAsync<InvalidOperationException>(() => AsBootstrap(circuit)
            .SetDisabledAsync(UserId.New(), disabled: true, TestContext.Current.CancellationToken));

        ended.ShouldBe(calls);
        calls.ShouldHaveSingleItem();
    }

    /// <summary>The public interface, resolved from <paramref name="circuit"/> with the bootstrap administrator published.</summary>
    /// <param name="circuit">The long-lived scope standing for a circuit.</param>
    /// <returns>The guarded administration.</returns>
    private static IAlvoUserAdministration AsBootstrap(IServiceScope circuit)
    {
        circuit.ServiceProvider.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext { User = _bootstrap, Roles = new HashSet<Role> { Role.Authenticated } },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "test:bootstrap",
        };

        return circuit.ServiceProvider.GetRequiredService<IAlvoUserAdministration>();
    }

    /// <summary><c>AddAlvo</c> with a recording implementation under the unguarded key.</summary>
    /// <param name="calls">Receives the scope of every call the implementation served.</param>
    /// <param name="ended">Receives the scope of every implementation instance disposed.</param>
    /// <param name="fail">Whether every write throws.</param>
    /// <returns>The container.</returns>
    private static ServiceProvider Container(List<ScopeTag> calls, List<ScopeTag> ended, bool fail = false)
    {
        var bootstrap = Substitute.For<IAlvoBootstrapAdmin>();
        bootstrap.IsBootstrapAdmin(Arg.Any<UserId>()).Returns(call => call.Arg<UserId>() == _bootstrap);

        var services = new ServiceCollection();
        services.AddAlvo();
        services.Replace(ServiceDescriptor.Singleton(bootstrap));
        services.AddScoped<ScopeTag>();
        services.AddKeyedScoped<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey,
            (scope, _) => new RecordingAdministration(scope.GetRequiredService<ScopeTag>(), calls, ended, fail));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>One instance per scope, so a call can say which scope served it.</summary>
    internal sealed class ScopeTag;

    /// <summary>An implementation that records the scope of every call and of its own disposal.</summary>
    private sealed class RecordingAdministration(
        ScopeTag scope, List<ScopeTag> calls, List<ScopeTag> ended, bool fail) : IAlvoUserAdministration, IDisposable
    {
        public Task<AlvoUserPage> ListAsync(AlvoUserQuery query, CancellationToken cancellationToken = default)
            => Record(new AlvoUserPage([], null, 0));

        public Task<AlvoUser> CreateAsync(AlvoUserCreation creation, CancellationToken cancellationToken = default)
            => Record(Person());

        public Task<AlvoUser> SetRolesAsync(
            UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default)
            => Record(Person());

        public Task<AlvoUser> SetTenantAsync(UserId user, TenantId? tenant, CancellationToken cancellationToken = default)
            => Record(Person());

        public Task<AlvoUser> SetDisabledAsync(UserId user, bool disabled, CancellationToken cancellationToken = default)
            => Record(Person());

        public Task<AlvoUser> ClearLockoutAsync(UserId user, CancellationToken cancellationToken = default)
            => Record(Person());

        public Task<AlvoCredentialToken> IssueCredentialTokenAsync(UserId user, CancellationToken cancellationToken = default)
            => Record(new AlvoCredentialToken(user, "token", DateTimeOffset.UtcNow));

        public void Dispose() => ended.Add(scope);

        private Task<T> Record<T>(T answer)
        {
            calls.Add(scope);
            return fail && answer is not AlvoUserPage
                ? Task.FromException<T>(new InvalidOperationException("refused"))
                : Task.FromResult(answer);
        }

        private static AlvoUser Person() => new()
        {
            Id = UserId.New(),
            Email = "someone@example.test",
            RoleNames = [],
            IsDisabled = false,
            Tenant = null,
        };
    }
}
