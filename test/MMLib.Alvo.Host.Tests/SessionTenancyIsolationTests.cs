using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Identity;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// What a <b>signed-in operator</b> reaches, adversarially: two people, two tenants, one entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every cross-tenant fact in this repository was written about an API key.</b>
/// <c>AlvoUser.Tenant</c> gave <see cref="AlvoContext.Tenant"/> a second provenance — a user row an
/// administrator edits through <see cref="IAlvoUserAdministration"/>, resolved by a cookie session
/// rather than by a key — and a second provenance is a second place the predicate can be lost. So
/// the same adversarial shape runs again over the new one: the tests below never assert that "the
/// resolver returns the right tenant", they assert that one operator cannot see, fetch or count
/// the other's rows.
/// </para>
/// <para>
/// <b>Through <see cref="IAlvoData"/>, which is the dashboard's own path to rows.</b> Design §2.4
/// and deviation D4: the Management API has no data surface, so the dashboard reaches records
/// through the data port with the operator's own context and nothing else. Measuring the port with
/// a session-resolved context is therefore measuring what the dashboard does — one layer below the
/// browser, where the predicate either is in the SQL or is not.
/// </para>
/// <para>
/// <b>The entity's rules deliberately admit <c>authenticated</c>.</b> If a rule separated the two
/// operators, a passing test would prove the rule ran and say nothing about the tenant predicate —
/// which is the only thing standing between two tenants on one table.
/// </para>
/// </remarks>
public sealed class SessionTenancyIsolationTests : IAsyncLifetime
{
    private const string Descriptor = "host-session-tenancy.alvo.json";

    private AlvoHostWorld? _world;
    private IServiceScope? _scope;
    private IAlvoData _data = null!;
    private IAlvoContextResolver _sessions = null!;
    private IAlvoUserAdministration _people = null!;

    private readonly TenantId _north = TenantId.New();
    private readonly TenantId _south = TenantId.New();

    private AlvoContext _ada = null!;
    private AlvoContext _bruno = null!;
    private AlvoContext _clara = null!;

    private UserId _adaId;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _world = await AlvoHostWorld.StartAsync(Descriptor);
        _scope = _world.Services.CreateScope();

        _data = _scope.ServiceProvider.GetRequiredService<IAlvoData>();
        _sessions = _scope.ServiceProvider.GetRequiredKeyedService<IAlvoContextResolver>(
            AlvoIdentity.ResolverKey);
        _people = _scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey);

        _adaId = (await CreateAsync("ada@alvo.test", _north)).Id;
        _ada = await SessionOfAsync(_adaId);
        _bruno = await SessionOfAsync((await CreateAsync("bruno@alvo.test", _south)).Id);
        _clara = await SessionOfAsync((await CreateAsync("clara@alvo.test", tenant: null)).Id);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _scope?.Dispose();
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }

    [Fact]
    public async Task One_operators_rows_are_invisible_to_the_other()
    {
        await NoteAsync(_ada, "north note");
        await NoteAsync(_bruno, "south note");

        (await TitlesAsync(_ada)).ShouldBe(["north note"]);
        (await TitlesAsync(_bruno)).ShouldBe(["south note"]);
    }

    [Fact]
    public async Task A_row_id_learned_out_of_band_still_does_not_fetch()
    {
        var mine = await NoteAsync(_ada, "north note");

        (await _data.GetAsync("notes", mine, _bruno, TestContext.Current.CancellationToken))
            .ShouldBeNull(
                "knowing a row's id is not authority to read it — the tenant predicate is in the WHERE, "
                + "so the row is not found rather than found and filtered");
    }

    [Fact]
    public async Task A_total_count_does_not_leak_the_other_tenants_rows()
    {
        await NoteAsync(_ada, "one");
        await NoteAsync(_ada, "two");
        await NoteAsync(_bruno, "three");

        var page = await _data.QueryAsync(
            new AlvoQuery { Entity = "notes", Limit = 50, IncludeTotalCount = true },
            _bruno,
            TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(
            1,
            "the count is over the policy-filtered set; a count taken before the predicate would tell "
            + "one tenant how many rows the other has");
    }

    [Fact]
    public async Task A_write_cannot_be_aimed_at_another_tenants_row()
    {
        var mine = await NoteAsync(_ada, "north note");

        /* Not found rather than forbidden, and that is the stronger answer: telling the two apart
           would let a caller enumerate the rows of a tenant they cannot read. */
        await Should.ThrowAsync<AlvoRecordNotFoundException>(
            () => _data.UpdateAsync(
                "notes",
                mine,
                new Dictionary<string, object?> { ["title"] = "taken" },
                _bruno,
                cancellationToken: TestContext.Current.CancellationToken));

        var after = await _data.GetAsync("notes", mine, _ada, TestContext.Current.CancellationToken);
        after.ShouldNotBeNull()["title"].ShouldBe("north note");
    }

    [Fact]
    public async Task An_operator_with_no_tenant_is_refused_a_scoped_entity_rather_than_widened_to_all()
    {
        await NoteAsync(_ada, "north note");
        await NoteAsync(_bruno, "south note");

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => _data.QueryAsync(
                new AlvoQuery { Entity = "notes", Limit = 50 },
                _clara,
                TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain(
            "no tenant",
            Shouldly.Case.Insensitive,
            "the absence of a tenant denies outright; an empty page would be the same answer a tenant "
            + "with no rows gets, and the widening reading would make it a cross-tenant read");
    }

    [Fact]
    public async Task The_refusal_is_about_the_tenant_and_not_about_the_caller()
    {
        var announcement = await _data.CreateAsync(
            "announcements",
            new Dictionary<string, object?> { ["body"] = "everyone reads this" },
            _clara,
            cancellationToken: TestContext.Current.CancellationToken);

        var page = await _data.QueryAsync(
            new AlvoQuery { Entity = "announcements", Limit = 50 },
            _clara,
            TestContext.Current.CancellationToken);

        page.Items.Select(row => row["body"] as string).ShouldContain("everyone reads this");
        announcement["id"].ShouldNotBeNull(
            "the same caller the scoped entity refuses writes and reads a global one, which is what "
            + "tells 'this operator holds no tenant' apart from 'this operator was not admitted' — "
            + "merging the two is how a tenancy bug reads as an authentication bug");
    }

    [Fact]
    public async Task An_operator_cannot_choose_a_tenant_they_were_not_granted()
    {
        var stolen = await _sessions.ResolveAsync(
            _adaId.ToString(), _south.ToString(), TestContext.Current.CancellationToken);

        stolen.ShouldBeNull(
            "the requested tenant is a confirmation, never a choice; and the refusal is the whole "
            + "principal, because one with a null tenant would still reach every global entity");
    }

    [Fact]
    public async Task A_revoked_tenant_takes_effect_on_the_next_resolve()
    {
        await NoteAsync(_ada, "north note");

        await _people.SetTenantAsync(_adaId, null, TestContext.Current.CancellationToken);
        var after = await SessionOfAsync(_adaId);

        after.Tenant.ShouldBeNull();
        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => _data.QueryAsync(
                new AlvoQuery { Entity = "notes", Limit = 50 },
                after,
                TestContext.Current.CancellationToken));

        /* The point of the fact: a context resolved once and held for the life of a circuit would
           still carry the revoked tenant, and would still reach the rows. */
    }

    private Task<AlvoUser> CreateAsync(string email, TenantId? tenant)
        => _people.CreateAsync(
            new AlvoUserCreation(email, ["operator"], tenant), TestContext.Current.CancellationToken);

    private async Task<AlvoContext> SessionOfAsync(UserId user)
    {
        var principal = await _sessions.ResolveAsync(
            user.ToString(), null, TestContext.Current.CancellationToken);

        return principal.ShouldNotBeNull().Context;
    }

    /// <summary>Creates one note as the given caller.</summary>
    /// <remarks>
    /// <b><c>tenant_id</c> is sent, and it has to be.</b> A create on a tenant-scoped entity is
    /// checked against the policy's <c>WITH CHECK</c>, which compares the post-image's tenant to
    /// the caller's — so a row that names no tenant is refused rather than silently stamped. That
    /// is the framework's rule, not this suite's convenience: it is the same body the Data API
    /// sends, and it is why an operator with no tenant cannot write a scoped row at all.
    /// </remarks>
    /// <param name="caller">Who is writing.</param>
    /// <param name="title">The note's title.</param>
    /// <returns>The new row's id.</returns>
    private async Task<Guid> NoteAsync(AlvoContext caller, string title)
    {
        var created = await _data.CreateAsync(
            "notes",
            new Dictionary<string, object?>
            {
                ["title"] = title,
                ["tenant_id"] = caller.Tenant?.Value,
            },
            caller,
            cancellationToken: TestContext.Current.CancellationToken);

        return (Guid)created["id"]!;
    }

    private async Task<IReadOnlyList<string?>> TitlesAsync(AlvoContext caller)
    {
        var page = await _data.QueryAsync(
            new AlvoQuery { Entity = "notes", Limit = 50 },
            caller,
            TestContext.Current.CancellationToken);

        return [.. page.Items.Select(row => row["title"] as string)];
    }
}
