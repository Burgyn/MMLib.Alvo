using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Extensions;
using NSubstitute;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>A core composition with write hooks over <see cref="HandlerCatalog"/>'s schema, a fake unit on the flow and a recording rejection log.</summary>
internal sealed class RunnerWorld : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private RunnerWorld(ServiceProvider provider, RecordingRejectionLog rejections)
    {
        _provider = provider;
        Rejections = rejections;
    }

    internal static AlvoContext Caller { get; } = AlvoContext.System(new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")));

    internal static Guid TicketId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    internal RecordingRejectionLog Rejections { get; }

    internal IWriteExtensionRunner Runner => _provider.GetRequiredService<IWriteExtensionRunner>();

    internal IServiceProvider Services => _provider;

    /// <summary>
    /// Starts a world on <paramref name="time"/>, or on a fake clock that never moves unless a test advances it: a hook's
    /// timeout on the real clock fires under parallel load and outranks the rejection a fact asserts (Ruling 51).
    /// </summary>
    internal static RunnerWorld Start(Action<IAlvoBuilder> configure, TimeProvider? time = null, Action<IServiceCollection>? services = null)
    {
        var log = new RecordingRejectionLog();
        var collection = new ServiceCollection();
        collection.AddSingleton(time ?? new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        collection.AddSingleton(Substitute.For<IAlvoTransactions>());
        collection.AddSingleton<IHookRejectionLog>(log);
        services?.Invoke(collection);
        collection.AddAlvo(configure);
        var world = new RunnerWorld(collection.BuildServiceProvider(), log);
        world._provider.GetRequiredService<IPolicyCatalogProvider>().SetCurrent("handlers", HandlerCatalog.Catalog());

        return world;
    }

    internal WriteInvocation Before(AlvoRecord @new, AlvoRecord? old = null, IAlvoTransaction? transaction = null) => new()
    {
        Entity = "tickets",
        Operation = old is null ? AlvoWriteOperation.Create : AlvoWriteOperation.Update,
        Caller = Caller,
        Now = DateTimeOffset.UnixEpoch,
        Old = old,
        New = @new,
        Transaction = transaction ?? new FakeHookTransaction(Caller),
        Services = _provider,
    };

    internal static AlvoRecord Ticket(string status = "open", string title = "t") => new(new Dictionary<string, object?>
    {
        ["id"] = TicketId,
        ["title"] = title,
        ["status"] = status,
    });

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>A rejection log that keeps what the runner hands it.</summary>
internal sealed class RecordingRejectionLog : IHookRejectionLog
{
    internal ConcurrentQueue<HookRejection> Recorded { get; } = new();

    public Task RecordAsync(HookRejection rejection, CancellationToken cancellationToken)
    {
        Recorded.Enqueue(rejection);
        return Task.CompletedTask;
    }
}
