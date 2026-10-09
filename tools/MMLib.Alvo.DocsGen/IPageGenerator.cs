using MMLib.Alvo.DocsGen.Host;

namespace MMLib.Alvo.DocsGen;

internal interface IPageGenerator
{
    Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct);
}

internal sealed class DocsGenContext(DocsGenPaths paths)
{
    internal DocsGenPaths Paths { get; } = paths;

    internal Lazy<Task<HostSnapshot>> Host { get; } = new(() => HostCapture.CaptureAsync(paths.RepoRoot, CancellationToken.None));
}
