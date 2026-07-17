namespace MMLib.Alvo.Testing;

/// <summary>
/// Locates the repository root for tests that scan project and solution files on
/// disk (the file-scanning "os A" convention tests), so they never depend on a
/// build having run or on any assembly being loaded.
/// </summary>
public static class RepositoryRoot
{
    private const string SolutionFileName = "MMLib.Alvo.slnx";

    /// <summary>
    /// Walks up from the test's base directory until it finds the directory that
    /// contains <c>MMLib.Alvo.slnx</c>.
    /// </summary>
    /// <returns>Absolute path to the repository root directory.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The solution file was not found in any ancestor directory.
    /// </exception>
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate '{SolutionFileName}' walking up from '{AppContext.BaseDirectory}'.");
    }
}
