using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

// EF1001 matches on the namespace ending in ".Internal", so here it flags Alvo's OWN internals rather than an
// Entity Framework internal API — both driver test projects are granted them by InternalsVisibleTo. A
// file-scoped suppression rather than a project-wide NoWarn, which would also hide a genuine EF-internal use.
#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// A unit's savepoint disposed without being committed takes back what its call already wrote, and the unit goes on — on
/// whichever engine the deriving class supplies. The case EF's own <c>__EFSavePoint</c> cannot cover: the insert's
/// <c>SaveChanges</c> succeeded, so EF released its savepoint before anything failed.
/// </summary>
public abstract class UnitSavepointFacts
{
    private const string Entity = "codes";

    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Boots a host over <paramref name="schema"/> and <paramref name="descriptor"/> and answers its services.</summary>
    protected abstract Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor);

    [Fact]
    public async Task A_savepoint_disposed_without_commit_takes_its_insert_back_and_the_unit_goes_on()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var data = (EfAlvoData)services.GetRequiredService<IAlvoData>();
        var contexts = services.GetRequiredService<AlvoDataContextFactory>();
        await using var unit = await data.OpenUnitAsync(_caller, new AlvoTransactionOptions(), services.GetRequiredService<IServiceScopeFactory>(), Ct);

        using (unit.EnterFlow())
        {
            await unit.RunAsync(
                async ct =>
                {
                    await InsertAsync(contexts, unit, "undone", commit: false, ct);
                    await InsertAsync(contexts, unit, "kept", commit: true, ct);
                    return true;
                },
                Ct);
        }

        await unit.CommitAsync();

        var page = await data.QueryAsync(new AlvoQuery { Entity = Entity }, _caller, Ct);
        page.Items.Select(row => (string)row["code"]!).ShouldBe(["kept"]);
    }

    private static async Task InsertAsync(AlvoDataContextFactory contexts, WriteUnit unit, string code, bool commit, CancellationToken ct)
    {
        using var db = contexts.Create();
        db.Database.SetDbConnection(unit.Connection, contextOwnsConnection: false);
        await using var savepoint = await UnitSavepoint.JoinAsync(db, unit, ct);
        db.Rows(Entity).Add(new Dictionary<string, object>(StringComparer.Ordinal) { ["id"] = Guid.NewGuid(), ["code"] = code });
        await db.SaveChangesAsync(ct);
        if (commit)
        {
            await savepoint.CommitAsync(ct);
        }
    }

    private static SchemaModel Schema() => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "code", Type = SchemaField.String, Required = true, MaxLength = 40 },
            ],
        },
    ]);

    private static AlvoDescriptor Descriptor() => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "unit-savepoint-fixture",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new EntityDescriptor
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["code"] = new() { Type = DescField.String, Required = true, MaxLength = 40 },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
            },
        },
    };
}
