using MMLib.Alvo.Testing;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The change tracker is reachable from inside this package — <see cref="EfAlvoData"/> holds a live
/// <c>AlvoDataContext</c> — and a tracked write bypasses policy completely: <c>Attach</c> + set +
/// <c>SaveChanges</c> emits <c>UPDATE … WHERE id = @p</c> with no predicate at all (spike <c>Q5d</c>). These
/// facts keep that unreachable by construction rather than by reviewer memory.
/// </summary>
/// <remarks>
/// <para>
/// A <b>source</b> scan rather than an architecture test over IL, and rather than a public-surface rule. The
/// constraint is "this call does not appear here", which IL-level type dependencies cannot express — the whole
/// package legitimately depends on <c>DbContext</c> — and the encapsulation rules about the public surface
/// cannot see a call inside an internal method. Its failure mode is also the right one: a contributor adding
/// the idiomatic tracked shape gets a named failure pointing at their own file.
/// </para>
/// <para>
/// Comment lines are stripped before matching, so the many remarks that <em>discuss</em> the tracked shape (and
/// exist precisely to warn about it) do not count as uses of it.
/// </para>
/// </remarks>
public class ChangeTrackerReachTests
{
    /// <summary>
    /// The insert is the one operation that legitimately saves, and the seeding seam is test-only and
    /// documented as bypassing policy. Nothing else in the package may.
    /// </summary>
    [Fact]
    public void Only_the_create_path_and_the_test_only_seed_reach_save_changes()
        => FilesMatching("SaveChanges").ShouldBe(["AlvoDataSeed.cs", "EfAlvoData.cs"], ignoreOrder: true);

    /// <summary>
    /// The tracked-write vocabulary, which has no legitimate use in this package at all: every one of these
    /// builds its own <c>WHERE</c> from the primary key, so the statement carries no policy predicate. An
    /// update or a delete goes through <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> over the <c>FromSql</c> root.
    /// </summary>
    [Theory]
    [InlineData(@"\.Attach(Range)?\(")]
    [InlineData(@"\.Update(Range)?\(")]
    [InlineData(@"\.Remove(Range)?\(")]
    [InlineData(@"\.Entry\(")]
    public void No_tracked_write_vocabulary_appears_in_this_package(string call)
        => FilesMatching(call).ShouldBeEmpty();

    /// <summary>
    /// Adding a property bag to a set is the create path's own call, so it is allowed exactly where
    /// <c>SaveChanges</c> is — a third file reaching for it would be a second tracked write path.
    /// </summary>
    [Fact]
    public void Only_the_create_path_and_the_test_only_seed_add_a_tracked_row()
        => FilesMatching(@"Rows\([^)]*\)\.Add\(").ShouldBe(["AlvoDataSeed.cs", "EfAlvoData.cs"], ignoreOrder: true);

    /// <summary>
    /// The one legitimate <c>ChangeTracker</c> touch is the context's own no-tracking setting, so pinning where
    /// it may appear pins the setting too: a returned row can never become a tracked entity, and no other file
    /// may reach the tracker to undo that.
    /// </summary>
    [Fact]
    public void The_change_tracker_is_touched_only_where_tracking_is_turned_off()
        => FilesMatching(@"ChangeTracker\.").ShouldBe(["AlvoDataContext.cs"]);

    /// <summary>
    /// The positive control: the scan really reads this package's sources, so an empty result above means "not
    /// present" rather than "nothing was scanned".
    /// </summary>
    [Fact]
    public void The_scan_reads_the_packages_own_sources()
    {
        SourceFiles().Count.ShouldBeGreaterThan(10);
        FilesMatching("ExecuteUpdateAsync").ShouldBe(["EfAlvoData.cs"]);
        FilesMatching("ExecuteDeleteAsync").ShouldBe(["EfAlvoData.cs"]);
    }

    private static IReadOnlyList<string> FilesMatching(string pattern) =>
        [.. SourceFiles()
            .Where(file => Regex.IsMatch(Code(file), pattern, RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Select(file => Path.GetFileName(file))];

    /// <summary>
    /// The file's code, with whole-line comments removed. Blunt on purpose — a trailing comment on a line of
    /// code still counts — because the safe direction to be wrong in is "reports a use that is only discussed",
    /// and the message names the file.
    /// </summary>
    private static string Code(string file) => string.Join(
        '\n',
        File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static IReadOnlyList<string> SourceFiles() =>
        [.. Directory.EnumerateFiles(PackageDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsGenerated(file))];

    private static bool IsGenerated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string PackageDirectory() =>
        Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Data.EntityFrameworkCore");
}
