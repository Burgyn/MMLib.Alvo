using MMLib.Alvo.Testing;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The change tracker is reachable from inside the data packages — <see cref="EfAlvoData"/> holds a live
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
/// <para>
/// <b>All three EF-referencing packages are scanned</b>, not only the shared one. A tracked write is just as
/// complete a bypass in a driver package, and both drivers reference EF Core — a scan confined to
/// <c>Data.EntityFrameworkCore</c> would have been silent about either of them.
/// </para>
/// </remarks>
public class ChangeTrackerReachTests
{
    /// <summary>
    /// The insert is the one operation that legitimately saves, and the seeding seam is test-only and
    /// documented as bypassing policy. Nothing else in any data package may.
    /// </summary>
    [Fact]
    public void Only_the_create_path_and_the_test_only_seed_reach_save_changes()
        => FilesMatching("SaveChanges").ShouldBe(["AlvoDataSeed.cs", "EfAlvoData.cs"], ignoreOrder: true);

    /// <summary>
    /// The tracked-write vocabulary, which has no legitimate use in this package at all: every one of these
    /// builds its own <c>WHERE</c> from the primary key, so the statement carries no policy predicate. An
    /// update or a delete goes through <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> over the <c>FromSql</c> root.
    /// </summary>
    /// <param name="call">The banned call, as a regular expression.</param>
    [Theory]
    [InlineData(@"\.Attach(Range)?\(")]
    [InlineData(@"\.Update(Range)?\(")]
    [InlineData(@"\.Remove(Range)?\(")]
    [InlineData(@"\.Entry\(")]
    [InlineData(@"\.AsTracking\(")]
    [InlineData(@"\bEntityState\.")]
    [InlineData(@"\.State\s*=[^=]")]
    public void No_tracked_write_vocabulary_appears_in_any_data_package(string call)
        => FilesMatching(call).ShouldBeEmpty();

    /// <summary>
    /// Adding a property bag to a set is the create path's own call, so it is allowed exactly where
    /// <c>SaveChanges</c> is — a third file reaching for it would be a second tracked write path.
    /// </summary>
    /// <remarks>
    /// The set is matched both inline (<c>Rows(...).Add(</c>) and through a local named <c>rows</c>, which is
    /// how this package spells it when one set is used twice. A file reaching the set under some third name
    /// would slip past this fact alone — but it would still have to call <c>SaveChanges</c> to persist
    /// anything, and the fact above confines that by itself.
    /// </remarks>
    [Fact]
    public void Only_the_create_path_and_the_test_only_seed_add_a_tracked_row()
        => FilesMatching(@"(?:Rows\([^)]*\)|\brows)\.Add\(")
            .ShouldBe(["AlvoDataSeed.cs", "EfAlvoData.cs"], ignoreOrder: true);

    /// <summary>
    /// The one legitimate <c>ChangeTracker</c> touch is the context's own no-tracking setting, so pinning where
    /// it may appear pins the setting too: a returned row can never become a tracked entity, and no other file
    /// may reach the tracker to undo that.
    /// </summary>
    [Fact]
    public void The_change_tracker_is_touched_only_where_tracking_is_turned_off()
        => FilesMatching(@"ChangeTracker\.").ShouldBe(["AlvoDataContext.cs"]);

    /// <summary>
    /// The positive control: the scan really reads all three packages' sources, so an empty result above means
    /// "not present" rather than "nothing was scanned". Each driver is named individually, because a wrong path
    /// would silently drop one and leave the totals plausible.
    /// </summary>
    [Fact]
    public void The_scan_reads_every_data_packages_own_sources()
    {
        var scanned = SourceFiles().Select(Path.GetFileName).ToList();

        scanned.Count.ShouldBeGreaterThan(20);
        scanned.ShouldContain("EfAlvoData.cs");
        scanned.ShouldContain("SqliteSqlDialect.cs");
        scanned.ShouldContain("PostgreSqlSqlDialect.cs");
        FilesMatching("ExecuteUpdateAsync").ShouldBe(["EfAlvoData.cs"]);
        FilesMatching("ExecuteDeleteAsync").ShouldBe(["EfAlvoData.cs"]);
    }

    /// <summary>
    /// The negative control for the vocabulary itself: every banned pattern must actually match the call it
    /// names. A typo in one of them (a stray escape, a wrong word) would make that row silently unenforceable,
    /// which is the failure mode a list of regular expressions has.
    /// </summary>
    /// <param name="call">The banned call, as a regular expression.</param>
    /// <param name="sample">A line the pattern must match.</param>
    [Theory]
    [InlineData(@"\.Attach(Range)?\(", "db.Attach(row);")]
    [InlineData(@"\.Attach(Range)?\(", "db.AttachRange(rows);")]
    [InlineData(@"\.Update(Range)?\(", "db.Update(row);")]
    [InlineData(@"\.Remove(Range)?\(", "db.Remove(row);")]
    [InlineData(@"\.Entry\(", "db.Entry(row).State = EntityState.Modified;")]
    [InlineData(@"\.AsTracking\(", "var row = db.Rows(entity).AsTracking().First();")]
    [InlineData(@"\bEntityState\.", "entry.State = EntityState.Modified;")]
    [InlineData(@"\.State\s*=[^=]", "entry.State = EntityState.Modified;")]
    public void Every_banned_pattern_matches_the_call_it_names(string call, string sample)
        => Regex.IsMatch(sample, call, RegexOptions.None, TimeSpan.FromSeconds(5)).ShouldBeTrue();

    /// <summary>
    /// And must not match the shapes this package legitimately uses, so a banned pattern cannot be satisfied by
    /// being unfalsifiable.
    /// </summary>
    /// <param name="call">The banned call, as a regular expression.</param>
    /// <param name="sample">A line the pattern must not match.</param>
    [Theory]
    [InlineData(@"\.State\s*=[^=]", "if (entry.State == EntityState.Added)")]
    [InlineData(@"\.AsTracking\(", "var rows = db.Rows(entity).AsNoTracking();")]
    [InlineData(@"\.Update(Range)?\(", "await root.ExecuteUpdateAsync(setters, cancellationToken);")]
    public void No_banned_pattern_matches_a_shape_this_package_uses(string call, string sample)
        => Regex.IsMatch(sample, call, RegexOptions.None, TimeSpan.FromSeconds(5)).ShouldBeFalse();

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
        [.. PackageDirectories()
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsGenerated(file))];

    private static bool IsGenerated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Every shipped package that references EF Core. The two drivers are here because a tracked write in one
    /// of them bypasses policy exactly as completely as one in the shared package would.
    /// </summary>
    private static IEnumerable<string> PackageDirectories() =>
        _efReferencingPackages.Select(package => Path.Combine(RepositoryRoot.Find(), "src", package));

    private static readonly string[] _efReferencingPackages =
        ["MMLib.Alvo.Data.EntityFrameworkCore", "MMLib.Alvo.Data.Sqlite", "MMLib.Alvo.Data.PostgreSql"];
}
