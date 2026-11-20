using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Where a created row on a tenant-scoped entity lands (#538): a payload that omits <c>tenant_id</c> lands in the
/// caller's own tenant, one naming the caller's tenant works as it always has, and one naming any other value is
/// refused with a sentence that says how to fix it — decided from the payload and the caller alone, so the answer
/// reports nothing about stored rows.
/// </summary>
public abstract partial class AlvoDataAdversarialTests
{
    /// <summary>The row lands in Acme, reads back there, and stays invisible to Globex.</summary>
    [Fact]
    public async Task A_scoped_create_without_tenant_id_lands_in_the_callers_own_tenant()
    {
        var fixture = await DocumentsFixtureAsync();
        var acmeUser = NewContext(fixture.Acme);

        var created = await fixture.Data.CreateAsync("documents", Untenanted("placed"), acmeUser);

        created[AlvoManagedColumns.TenantId].ShouldBe(fixture.Acme.Value);
        await ShouldBeAcmesOnlyAsync(fixture, (Guid)created[AlvoManagedColumns.Id]!);
    }

    /// <summary>The value is never echoed: the refusal names the field, not the tenant the caller tried.</summary>
    [Fact]
    public async Task A_scoped_create_naming_another_tenant_is_refused_with_how_to_fix_it()
    {
        var fixture = await DocumentsFixtureAsync();
        var payload = Tenanted("smuggled", fixture.Globex.Value);

        var refused = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => fixture.Data.CreateAsync("documents", payload, NewContext(fixture.Acme)));

        refused.Message.ShouldBe(AlvoAuthorizationException.ForeignTenant);
        refused.Message.ShouldNotContain(fixture.Globex.Value.ToString());
    }

    /// <summary>An explicit <see langword="null"/> is a value, and it is not the caller's tenant.</summary>
    [Fact]
    public async Task A_scoped_create_with_a_null_tenant_id_is_refused_the_same_way()
    {
        var fixture = await DocumentsFixtureAsync();

        var refused = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => fixture.Data.CreateAsync("documents", Tenanted("nulled", null), NewContext(fixture.Acme)));

        refused.Message.ShouldBe(AlvoAuthorizationException.ForeignTenant);
    }

    /// <summary>The backward-compatible shape: echoing the caller's own tenant keeps working.</summary>
    [Fact]
    public async Task A_scoped_create_naming_the_callers_own_tenant_still_succeeds()
    {
        var fixture = await DocumentsFixtureAsync();

        var created = await fixture.Data.CreateAsync(
            "documents", Tenanted("echoed", fixture.Acme.Value), NewContext(fixture.Acme));

        created[AlvoManagedColumns.TenantId].ShouldBe(fixture.Acme.Value);
        await ShouldBeAcmesOnlyAsync(fixture, (Guid)created[AlvoManagedColumns.Id]!);
    }

    /// <summary>The idempotent route places the row the same way, and its replay answers the same row.</summary>
    [Fact]
    public async Task An_idempotent_scoped_create_without_tenant_id_lands_in_the_callers_own_tenant()
    {
        var fixture = await DocumentsFixtureAsync();
        var acmeUser = NewContext(fixture.Acme);
        var token = new AlvoIdempotency(Guid.NewGuid().ToString("N"), "documents:untenanted");

        var created = await fixture.Data.CreateAsync("documents", Untenanted("keyed"), acmeUser, token);
        var replayed = await fixture.Data.CreateAsync("documents", Untenanted("keyed"), acmeUser, token);

        created[AlvoManagedColumns.TenantId].ShouldBe(fixture.Acme.Value);
        replayed[AlvoManagedColumns.Id].ShouldBe(created[AlvoManagedColumns.Id]);
        await ShouldBeAcmesOnlyAsync(fixture, (Guid)created[AlvoManagedColumns.Id]!);
    }

    /// <summary>Every row of a batch create that omits <c>tenant_id</c> lands in the caller's tenant.</summary>
    [Fact]
    public async Task A_scoped_batch_create_without_tenant_id_places_every_row_in_the_callers_own_tenant()
    {
        var fixture = await DocumentsFixtureAsync();

        var result = await fixture.Data.CreateManyAsync(
            "documents", [Untenanted("first"), Tenanted("second", fixture.Acme.Value)], NewContext(fixture.Acme));

        result.Succeeded.ShouldBeTrue();
        result.Rows.ShouldAllBe(row => Equals(row[AlvoManagedColumns.TenantId], fixture.Acme.Value));
        foreach (var row in result.Rows)
        {
            await ShouldBeAcmesOnlyAsync(fixture, (Guid)row[AlvoManagedColumns.Id]!);
        }
    }

    /// <summary>A batch row naming another tenant is refused at its own index, with the single write's sentence.</summary>
    [Fact]
    public async Task A_scoped_batch_create_row_naming_another_tenant_is_refused_at_its_index()
    {
        var fixture = await DocumentsFixtureAsync();

        var result = await fixture.Data.CreateManyAsync(
            "documents", [Untenanted("fine"), Tenanted("smuggled", fixture.Globex.Value)], NewContext(fixture.Acme));

        var refusal = result.Refusals.ShouldHaveSingleItem();
        refusal.Index.ShouldBe(1);
        refusal.Code.ShouldBe("forbidden");
        refusal.Message.ShouldBe(AlvoAuthorizationException.ForeignTenant);
    }

    private static async Task ShouldBeAcmesOnlyAsync(DocumentsFixture fixture, Guid id)
    {
        (await fixture.Data.GetAsync("documents", id, NewContext(fixture.Acme))).ShouldNotBeNull();
        (await fixture.Data.GetAsync("documents", id, NewContext(fixture.Globex))).ShouldBeNull();
    }

    private static Dictionary<string, object?> Untenanted(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title };

    private static Dictionary<string, object?> Tenanted(string title, Guid? tenant) =>
        new(StringComparer.Ordinal) { ["title"] = title, [AlvoManagedColumns.TenantId] = tenant };
}
