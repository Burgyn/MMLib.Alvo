using MMLib.Alvo.Migrations;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Secrets.Internal;
using MMLib.Alvo.Testing.Secrets;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// A missing required secret refuses a code-first boot, and stands a dashboard-first boot down — Failed, nothing primed, the
/// process alive — when the operator can add the secret in this process, so Settings → Secrets stays reachable.
/// </summary>
public sealed class AlvoBootServiceRequiredSecretTests
{
    [Fact]
    public async Task A_code_first_boot_missing_a_required_secret_is_refused()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.RequiredSecrets = Missing(new InMemorySecretStore());

        var refusal = (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>();

        refusal.Message.ShouldContain("'missing-secret'");
        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
    }

    [Fact]
    public async Task A_dashboard_first_boot_with_a_writable_store_stands_down_instead_of_exiting()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.City);
        world.RequiredSecrets = Missing(new InMemorySecretStore());

        (await world.BootAsync()).ShouldBeNull("the process stays up so the secret can be added in the dashboard");

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("'missing-secret'");
        world.State.SecretStandDown.ShouldNotBeNull().ShouldContain("'missing-secret'");
        world.PrimedEntities.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dashboard_first_boot_whose_store_cannot_take_the_secret_is_refused()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.City);
        world.RequiredSecrets = Missing(new ReadOnlyStore());

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>();
    }

    [Fact]
    public async Task A_boot_holding_every_required_secret_is_ready()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(SecretName.Parse("missing-secret"), "now-present", TestContext.Current.CancellationToken);
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.RequiredSecrets = Missing(store);

        await world.BootOrFailAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Ready);
    }

    private static RequiredSecretsCheck Missing(ISecretStore store) => new([new RequiredSecret(SecretName.Parse("missing-secret"))], store);

    private static BootServiceWorld Dashboard()
    {
        var world = new BootServiceWorld();
        world.Options.Project = BootServiceWorld.Project;

        return world;
    }

    private sealed class ReadOnlyStore : ISecretStore
    {
        public bool CanWrite => false;

        public ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);

        public ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<SecretName>>([]);
    }
}
