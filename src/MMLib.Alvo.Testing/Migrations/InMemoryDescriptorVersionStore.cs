using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>A DB-less append-only <see cref="IDescriptorVersionStore"/> fake for tests.</summary>
public sealed class InMemoryDescriptorVersionStore : IDescriptorVersionStore
{
    private readonly Dictionary<string, List<DescriptorVersion>> _history = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public Task<DescriptorVersion?> GetCurrentAsync(string project, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Current(project));
        }
    }

    /// <inheritdoc/>
    public Task<DescriptorVersion?> GetAsync(string project, int revision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var version = History(project).FirstOrDefault(v => v.Revision == revision);
            return Task.FromResult(version);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DescriptorVersion>> ListAsync(string project, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DescriptorVersion>>([.. History(project)]);
        }
    }

    /// <inheritdoc/>
    public Task<DescriptorVersion> AppendAsync(string project, DescriptorVersion candidate, int expectedRevision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(candidate);

        lock (_gate)
        {
            var current = Current(project)?.Revision ?? 0;
            if (current != expectedRevision)
            {
                throw new DescriptorConcurrencyException(project, expectedRevision, current);
            }

            var appended = candidate with { Revision = expectedRevision + 1 };
            History(project).Add(appended);
            return Task.FromResult(appended);
        }
    }

    private DescriptorVersion? Current(string project) => History(project).LastOrDefault();

    private List<DescriptorVersion> History(string project)
    {
        if (!_history.TryGetValue(project, out var list))
        {
            list = [];
            _history[project] = list;
        }

        return list;
    }
}
