namespace MMLib.Alvo.Testing;

/// <summary>
/// The repository's own <c>examples/</c> corpus, and which of those examples a host can actually
/// <b>apply</b>.
/// </summary>
/// <remarks>
/// <para>
/// One enumeration, read by every suite that needs it — the schema corpus validating each example against
/// <c>project.schema.json</c>, and the mapper suite asserting each <em>runnable</em> one applies without
/// refusal. A second <c>Directory.EnumerateFiles</c> beside it is how one suite comes to cover a file the
/// other does not, which is the same argument the format catalogue and the unhonoured-feature table each
/// settled the hard way.
/// </para>
/// <para>
/// <b>"Runnable" is marked in the tree, not in a list here.</b> An example that declares a feature the build
/// does not honour is refused at apply, so it is documentation of the descriptor <em>format</em> rather than
/// a backend anyone can start — and saying so needs to travel with the example, where a reader opening the
/// directory sees it. A list in this file would be invisible to exactly that reader, and would have to be
/// found and edited by whoever eventually makes the example runnable.
/// </para>
/// </remarks>
public static class AlvoExamples
{
    /// <summary>
    /// The file whose presence in an example's directory marks it as not applicable by this build, and says
    /// why.
    /// </summary>
    /// <remarks>
    /// A marker file rather than an attribute or a manifest entry, so deleting it is the whole act of
    /// declaring an example runnable again — and so the fact that reads it fails the moment the example
    /// <em>would</em> apply, forcing the marker out rather than letting it outlive its reason.
    /// </remarks>
    public static string NotRunnableMarker => "NOT-RUNNABLE.md";

    /// <summary>Every positive example descriptor, ordered stably.</summary>
    /// <remarks>
    /// Excludes <c>_negative/</c>, whose fixtures exist to be rejected and are enumerated separately by the
    /// schema corpus.
    /// </remarks>
    public static IEnumerable<string> Descriptors() =>
        Directory.EnumerateFiles(ExamplesDirectory, "*.alvo.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>Every example descriptor a host can apply — those whose directory carries no marker.</summary>
    public static IEnumerable<string> Runnable() =>
        Descriptors().Where(path => !IsMarkedNotRunnable(path));

    /// <summary>
    /// Every example descriptor deliberately kept un-appliable, so a fact can assert that each one really is
    /// refused rather than merely labelled.
    /// </summary>
    public static IEnumerable<string> NotRunnable() =>
        Descriptors().Where(IsMarkedNotRunnable);

    /// <summary>Whether the example at <paramref name="descriptorPath"/> is marked not runnable.</summary>
    /// <param name="descriptorPath">A path from <see cref="Descriptors"/>.</param>
    public static bool IsMarkedNotRunnable(string descriptorPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        var directory = Path.GetDirectoryName(descriptorPath)
            ?? throw new ArgumentException($"'{descriptorPath}' has no directory.", nameof(descriptorPath));

        return File.Exists(Path.Combine(directory, NotRunnableMarker));
    }

    private static string ExamplesDirectory => Path.Combine(RepositoryRoot.Find(), "examples");
}
