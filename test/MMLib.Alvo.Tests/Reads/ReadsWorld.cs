using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Reads.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;

using System.Collections.Frozen;

namespace MMLib.Alvo.Tests.Reads;

[AlvoEntity("notes")]
public sealed class ReadNote
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public Guid OwnerId { get; init; }

    public string? Region { get; init; }
}

/// <summary>A relational core composition over the reads-desk descriptor, with the read runner and a captured log.</summary>
internal sealed class ReadsWorld : IAsyncDisposable
{
    internal const string Descriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "reads-desk",
          "tenancy": { "enabled": true },
          "auth": { "roles": ["clerk", "auditor"] },
          "entities": {
            "notes": {
              "tenancy": "scoped",
              "fields": {
                "title": { "type": "string", "required": true, "maxLength": 80 },
                "owner_id": { "type": "uuid", "required": true },
                "region": { "type": "string", "maxLength": 20 },
                "salary": { "type": "integer", "hidden": "!('auditor' in @user.roles)" }
              },
              "rules": { "list": "true", "get": "true" }
            },
            "tickets": {
              "fields": { "title": { "type": "string" } },
              "rules": { "list": "true", "get": "true" }
            }
          }
        }
        """;

    private readonly ServiceProvider _provider;

    private ReadsWorld(ServiceProvider provider, CapturingLogger logs)
    {
        _provider = provider;
        Logs = logs;
    }

    internal static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated }.ToFrozenSet(),
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")),
    };

    internal CapturingLogger Logs { get; }

    internal IReadExtensionRunner Runner => _provider.GetRequiredService<IReadExtensionRunner>();

    /// <summary>A runner over this world's registry and catalog, opening its scopes from <paramref name="scopes"/>.</summary>
    internal IReadExtensionRunner RunnerOver(IServiceScopeFactory scopes) =>
        ActivatorUtilities.CreateInstance<ReadExtensionRunner>(_provider, scopes);

    /// <summary>This world's own scope factory, counting the scopes a runner opens through it.</summary>
    internal PassThroughScopes CountingScopes() => new(_provider.GetRequiredService<IServiceScopeFactory>());

    internal static ReadsWorld Start(Action<IAlvoBuilder> configure, PolicyCatalog? catalog = null, Action<IServiceCollection>? services = null)
    {
        var logs = new CapturingLogger();
        var collection = new ServiceCollection();
        collection.AddLogging(logging => logging.AddProvider(logs));
        collection.AddSingleton(Substitute.For<IAlvoTransactions>());
        collection.AddSingleton<IHookRejectionLog>(new Hooks.RecordingRejectionLog());
        services?.Invoke(collection);
        collection.AddAlvo(configure);
        var world = new ReadsWorld(collection.BuildServiceProvider(), logs);
        world._provider.GetRequiredService<IPolicyCatalogProvider>().SetCurrent("reads-desk", catalog ?? Catalog());

        return world;
    }

    internal static PolicyCatalog Catalog(string descriptor = Descriptor)
    {
        var parsed = AlvoDescriptor.Parse(descriptor);
        return PolicyCatalog.Build(parsed, DescriptorToSchemaMapper.Map(parsed), CelFixtures.Compiler);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>A real scope factory that counts the scopes opened through it.</summary>
internal sealed class PassThroughScopes(IServiceScopeFactory inner) : IServiceScopeFactory
{
    internal int Created { get; private set; }

    public IServiceScope CreateScope()
    {
        Created++;
        return inner.CreateScope();
    }
}
