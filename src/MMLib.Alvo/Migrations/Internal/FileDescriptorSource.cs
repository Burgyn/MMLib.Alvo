namespace MMLib.Alvo.Migrations.Internal;

/// <summary>An <see cref="IDescriptorSource"/> that reads the project descriptor from a file on disk.</summary>
internal sealed class FileDescriptorSource(string path) : IDescriptorSource
{
    /// <inheritdoc/>
    public Task<string> LoadAsync(CancellationToken ct = default) => File.ReadAllTextAsync(path, ct);
}
