using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Identity.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// A temporary lockout from failed sign-ins is projected as its end and can be ended early by an administrator
/// (docs/todo-admin.md §8d items 39 and 46(i)), over a real SQLite file.
/// </summary>
/// <remarks>
/// The unguarded implementation, resolved by its key: the guard is the core's and is measured by the contract suite;
/// what is measured here is what the store writes and reads.
/// </remarks>
public sealed class AlvoIdentityUserAdministrationLockoutTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-identity-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;

    private IAlvoUserAdministration People => Unguarded(_scope);

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}"));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateScope();
        await _scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>().Database
            .EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _provider.DisposeAsync();
        /* This file's pool only: clearing every pool in the process races the other classes' open connections. */
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        File.Delete(_file);
    }

    /// <summary>A lockout in the future is its end on both reads, and the person is not disabled.</summary>
    [Fact]
    public async Task A_temporary_lockout_projects_its_end_and_is_not_a_disable()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        var person = await CreateAsync("locked@alvo.test");
        await WriteLockoutAsync(person, until, failedAttempts: 0);

        var listed = await ListedAsync(person);
        var found = await _scope.ServiceProvider.GetRequiredService<IAlvoUserStore>()
            .FindAsync(person, TestContext.Current.CancellationToken);

        listed.LockedOutUntil.ShouldNotBeNull().ShouldBe(until, TimeSpan.FromMilliseconds(1));
        listed.IsDisabled.ShouldBeFalse();
        found.ShouldNotBeNull().LockedOutUntil.ShouldNotBeNull().ShouldBe(until, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>A disable is not a lockout: it projects as disabled, with no end.</summary>
    [Fact]
    public async Task A_disabled_person_projects_no_lockout()
    {
        var person = await CreateAsync("disabled@alvo.test");
        await People.SetDisabledAsync(person, disabled: true, TestContext.Current.CancellationToken);

        var listed = await ListedAsync(person);

        listed.IsDisabled.ShouldBeTrue();
        listed.LockedOutUntil.ShouldBeNull();
    }

    /// <summary>A lockout that has already ended is not one.</summary>
    [Fact]
    public async Task A_lockout_that_has_ended_projects_nothing()
    {
        var person = await CreateAsync("was-locked@alvo.test");
        await WriteLockoutAsync(person, DateTimeOffset.UtcNow.AddMinutes(-1), failedAttempts: 0);

        (await ListedAsync(person)).LockedOutUntil.ShouldBeNull();
    }

    /// <summary>Clearing ends the lockout and forgets the failed attempts, in what it answers and in the store.</summary>
    [Fact]
    public async Task Clearing_ends_the_lockout_and_forgets_the_failed_attempts()
    {
        var person = await CreateAsync("clear@alvo.test");
        await WriteLockoutAsync(person, DateTimeOffset.UtcNow.AddMinutes(5), failedAttempts: 3);

        var answered = await People.ClearLockoutAsync(person, TestContext.Current.CancellationToken);

        answered.LockedOutUntil.ShouldBeNull();
        answered.IsDisabled.ShouldBeFalse();
        var stored = await StoredAsync(person);
        stored.LockoutEnd.ShouldBeNull();
        stored.AccessFailedCount.ShouldBe(0);
    }

    /// <summary>
    /// Clearing ends no session: the security stamp, which every cookie and credential token is checked against, is
    /// untouched (a disable rotates it; this is not a disable).
    /// </summary>
    [Fact]
    public async Task Clearing_leaves_the_security_stamp_alone()
    {
        var person = await CreateAsync("stamp@alvo.test");
        await WriteLockoutAsync(person, DateTimeOffset.UtcNow.AddMinutes(5), failedAttempts: 5);
        var before = (await StoredAsync(person)).SecurityStamp;

        await People.ClearLockoutAsync(person, TestContext.Current.CancellationToken);

        (await StoredAsync(person)).SecurityStamp.ShouldBe(before);
    }

    /// <summary>
    /// A disabled person is refused by name and stays disabled: an unlock that cleared the column a disable writes
    /// would let them back in by a door that decides nothing about a disable.
    /// </summary>
    [Fact]
    public async Task Clearing_refuses_a_disabled_person_and_leaves_them_disabled()
    {
        var person = await CreateAsync("stays-disabled@alvo.test");
        await People.SetDisabledAsync(person, disabled: true, TestContext.Current.CancellationToken);

        var refusal = await Should.ThrowAsync<ManagementRequestException>(
            () => People.ClearLockoutAsync(person, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("disabled");
        refusal.Message.ShouldContain("Let them back in");
        AlvoIdentityLockout.IsDisabled((await StoredAsync(person)).LockoutEnd).ShouldBeTrue();
        (await ListedAsync(person)).IsDisabled.ShouldBeTrue();
    }

    /// <summary>
    /// The check reads the person as stored at the call: a disable written from another scope after this one had the
    /// person tracked as merely locked out is still refused, never undone (the unit of work's fresh read).
    /// </summary>
    [Fact]
    public async Task Clearing_sees_a_disable_written_elsewhere_after_this_scope_read_the_person()
    {
        var person = await CreateAsync("raced@alvo.test");
        await WriteLockoutAsync(person, DateTimeOffset.UtcNow.AddMinutes(5), failedAttempts: 5);

        /* This scope holds the person tracked as they were: locked out, not disabled. */
        var users = _scope.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        (await users.FindByIdAsync(person.Value.ToString())).ShouldNotBeNull();

        using (var elsewhere = _provider.CreateScope())
        {
            await Unguarded(elsewhere).SetDisabledAsync(person, disabled: true, TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<ManagementRequestException>(
            () => People.ClearLockoutAsync(person, TestContext.Current.CancellationToken));
        AlvoIdentityLockout.IsDisabled((await StoredAsync(person)).LockoutEnd).ShouldBeTrue();
    }

    /// <summary>A person with no lockout is answered as they are: the call is safe to repeat.</summary>
    [Fact]
    public async Task Clearing_a_person_with_no_lockout_answers_them_as_they_are()
    {
        var person = await CreateAsync("free@alvo.test");

        var first = await People.ClearLockoutAsync(person, TestContext.Current.CancellationToken);
        var second = await People.ClearLockoutAsync(person, TestContext.Current.CancellationToken);

        first.LockedOutUntil.ShouldBeNull();
        second.Id.ShouldBe(person);
        second.LockedOutUntil.ShouldBeNull();
        second.IsDisabled.ShouldBeFalse();
    }

    /// <summary>Clearing leaves the change tracker empty, like every administration write.</summary>
    [Fact]
    public async Task Clearing_leaves_nothing_tracked()
    {
        var person = await CreateAsync("tracked@alvo.test");
        await WriteLockoutAsync(person, DateTimeOffset.UtcNow.AddMinutes(5), failedAttempts: 5);

        await People.ClearLockoutAsync(person, TestContext.Current.CancellationToken);

        _scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>().ChangeTracker.Entries().ShouldBeEmpty();
    }

    /// <summary>
    /// <b>A cancelled clear throws and writes nothing</b>: the token reaches the unit of work, so the lockout and the
    /// failed-attempt count stay as they were (final branch review, item 18).
    /// </summary>
    [Fact]
    public async Task A_cancelled_clear_writes_nothing()
    {
        var person = await CreateAsync("cancelled@alvo.test");
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        await WriteLockoutAsync(person, until, failedAttempts: 5);

        await Should.ThrowAsync<OperationCanceledException>(
            () => People.ClearLockoutAsync(person, new CancellationToken(canceled: true)));

        var stored = await StoredAsync(person);
        stored.LockoutEnd.ShouldNotBeNull().ShouldBe(until, TimeSpan.FromMilliseconds(1));
        stored.AccessFailedCount.ShouldBe(5);
    }

    /// <summary>A cancelled disable throws and writes nothing, like every administration write (item 18's siblings).</summary>
    [Fact]
    public async Task A_cancelled_disable_writes_nothing()
    {
        var person = await CreateAsync("cancelled-disable@alvo.test");
        var stamp = (await StoredAsync(person)).SecurityStamp;

        await Should.ThrowAsync<OperationCanceledException>(
            () => People.SetDisabledAsync(person, disabled: true, new CancellationToken(canceled: true)));

        var stored = await StoredAsync(person);
        AlvoIdentityLockout.IsDisabled(stored.LockoutEnd).ShouldBeFalse();
        stored.SecurityStamp.ShouldBe(stamp);
    }

    private static IAlvoUserAdministration Unguarded(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);

    private async Task<UserId> CreateAsync(string email)
        => (await People.CreateAsync(new AlvoUserCreation(email, []), TestContext.Current.CancellationToken)).Id;

    private async Task<AlvoUser> ListedAsync(UserId person)
    {
        var page = await People.ListAsync(new AlvoUserQuery(), TestContext.Current.CancellationToken);
        return page.Users.Single(user => user.Id == person);
    }

    /// <summary>Writes a lockout and a failed-attempt count as failed sign-ins would, in a scope of its own.</summary>
    private async Task WriteLockoutAsync(UserId person, DateTimeOffset until, int failedAttempts)
    {
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>();
        var row = await store.Users.SingleAsync(user => user.Id == person.Value, TestContext.Current.CancellationToken);
        row.LockoutEnabled = true;
        row.LockoutEnd = until;
        row.AccessFailedCount = failedAttempts;
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The person's row as stored now, read in a scope of its own.</summary>
    private async Task<AlvoIdentityUser> StoredAsync(UserId person)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>().Users.AsNoTracking()
            .SingleAsync(user => user.Id == person.Value, TestContext.Current.CancellationToken);
    }
}
