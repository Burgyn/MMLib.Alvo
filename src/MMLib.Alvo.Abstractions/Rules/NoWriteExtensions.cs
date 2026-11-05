using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>The runner a driver uses when no core runner is registered: no hook, no patch, no unit.</summary>
internal sealed class NoWriteExtensions : IWriteExtensionRunner
{
    private static readonly IReadOnlyDictionary<string, object?> _none = new Dictionary<string, object?>(StringComparer.Ordinal);

    private NoWriteExtensions()
    {
    }

    internal static NoWriteExtensions Instance { get; } = new();

    public bool Validates(string entity, AlvoWriteOperation operation) => false;

    public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => false;

    public Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken) =>
        Task.FromResult(_none);

    public Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
