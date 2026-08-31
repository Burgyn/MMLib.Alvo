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
    [MemberData(nameof(BannedCalls))]
    public void No_tracked_write_vocabulary_appears_in_any_data_package(string call)
        => FilesMatching(call).ShouldBeEmpty();

    /// <summary>
    /// The banned vocabulary, as regular expressions. Every call form tolerates an <b>explicit generic
    /// argument list</b>, because that is one keystroke away from the non-generic spelling and was a live
    /// bypass: <c>db.Rows(entity).AsTracking&lt;Dictionary&lt;string, object&gt;&gt;()</c> returns tracked rows,
    /// <c>SaveChanges</c> is already allow-listed in the create path, and a production file carrying that one
    /// line built with zero warnings and passed every fact in this class.
    /// </summary>
    /// <remarks>
    /// <c>&lt;[^&gt;]*&gt;</c> would stop at the first <c>&gt;</c> of a nested generic argument, so the
    /// argument list is matched as a run of characters that cannot contain a statement terminator — enough to
    /// span <c>&lt;Dictionary&lt;string, object&gt;&gt;</c> without needing balanced-bracket matching, which a
    /// regular expression cannot express anyway.
    /// </remarks>
    public static TheoryData<string> BannedCalls() => [.. _bannedCalls];

    private static readonly string[] _bannedCalls =
    [
        Generic(@"\.Attach(Range)?"),
        Generic(@"\.Update(Range)?"),
        Generic(@"\.Remove(Range)?"),
        Generic(@"\.Entry"),
        Generic(@"\.AsTracking"),
        @"\bEntityState\.",
        @"\.State\s*=[^=]",
    ];

    private static string Generic(string call) => call + GenericArgumentList + @"\s*\(";

    private const string GenericArgumentList = @"(\s*<[^;{}()]*>)?";

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
    /// A hand-built <c>DbCommand</c> is a complete policy bypass and a first-order injection surface, and it
    /// is <b>the house style of four files in these very packages</b> — so the shape a contributor reaches for
    /// by copying the file next door writes SQL with no predicate in it. This fact is an <b>allow-list</b>: the
    /// files permitted to compose SQL or construct a command are named, and any other file in a data package
    /// that does fails here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An allow-list rather than a ban-list, because a ban-list is a guess about what the next contributor
    /// will type. The bypass this closes — <c>GetDbConnection().CreateCommand()</c> plus a concatenated
    /// <c>UPDATE … WHERE id = '…'</c> in a new file — was in no banned vocabulary at all, built with zero
    /// warnings under <c>TreatWarningsAsErrors</c>, and passed every other fact in this class.
    /// </para>
    /// <para>
    /// Each allow-listed file earns its place by writing a <b>framework</b> table (the descriptor-version
    /// history, the system schema), by running SQL EF's own generator produced, or by being the one seam that
    /// binds parameters rather than composing a row statement:
    /// <c>EfCoreDescriptorVersionStore</c>, <c>EfCoreRuntimeSchemaWriter</c>, <c>SystemSchemaInitializer</c>,
    /// <c>IdempotencyTable</c>, <c>OutboxTable</c>, <c>RelationalSqlBatch</c> and <c>VersionRowWriter</c> never
    /// touch an entity table — <c>IdempotencyTable</c> in particular reads and writes only the
    /// idempotency-record table, and <c>OutboxTable</c> only the outbox table, and each exists as its own file
    /// precisely so the row-statement file does not also become the place framework bookkeeping SQL is written;
    /// <c>PredicateParameterBinder</c> creates a command only to reach the provider's parameter factory;
    /// <c>EfCoreSchemaMigrator</c> executes the migrator's generated statements;
    /// <c>SqliteCaseSensitiveLike</c> runs one connection pragma and can carry no row predicate at all;
    /// <c>RollupRecompute</c> writes the parent's own aggregate columns from a subquery over the child table
    /// and narrows by the row id it read off the child row this caller was already authorised to write, so it
    /// composes no caller-influenced predicate and every identifier in it comes from the dialect;
    /// <c>RelationalReachability</c> executes <c>IAlvoSqlDialect.ReachabilityProbeStatement</c> verbatim — a
    /// per-dialect constant that names no table, carries no <c>WHERE</c> and binds no parameter, so there is
    /// nothing in it a caller could influence and nothing for a policy predicate to be missing from; and
    /// <c>EfAlvoData</c> is the one file that composes a row statement, and it is the file every other fact
    /// here pins.
    /// </para>
    /// </remarks>
    /// <param name="call">A raw-SQL or raw-command call, as a regular expression.</param>
    [Theory]
    [InlineData(@"\.CreateCommand\s*\(")]
    [InlineData(@"\.CommandText\s*=[^=]")]
    [InlineData(@"\.ExecuteNonQuery")]
    [InlineData(@"\.ExecuteScalar")]
    [InlineData(@"\.ExecuteReader")]
    [InlineData(@"\.ExecuteSql(Raw|Interpolated)")]
    [InlineData(@"\.FromSql(Raw|Interpolated)")]
    [InlineData(@"\.GetDbConnection\s*\(")]
    public void Only_allow_listed_files_compose_sql_or_build_a_command(string call)
        => FilesMatching(call).ShouldBeSubsetOf(_sqlComposingFiles);

    /// <summary>
    /// The allow-list's own non-vacuity control: each pattern must really match the shape it names, so a file
    /// that is not allow-listed would be reported. Asserted against a sample line rather than by landing such a
    /// file, so the proof is permanent instead of reverted — and so no policy-free writer has to exist in a
    /// shipped package to keep the gate honest.
    /// </summary>
    /// <param name="call">A raw-SQL or raw-command call, as a regular expression.</param>
    /// <param name="sample">A line the pattern must match.</param>
    [Theory]
    [InlineData(@"\.CreateCommand\s*\(", "using var command = connection.CreateCommand();")]
    [InlineData(@"\.CommandText\s*=[^=]", "command.CommandText = sql;")]
    [InlineData(@"\.ExecuteNonQuery", "return await command.ExecuteNonQueryAsync();")]
    [InlineData(@"\.ExecuteScalar", "var count = await command.ExecuteScalarAsync();")]
    [InlineData(@"\.ExecuteReader", "await using var reader = await command.ExecuteReaderAsync();")]
    [InlineData(@"\.GetDbConnection\s*\(", "var connection = db.Database.GetDbConnection();")]
    [InlineData(@"\.ExecuteSql(Raw|Interpolated)", "await db.Database.ExecuteSqlRawAsync(sql);")]
    [InlineData(@"\.FromSql(Raw|Interpolated)", "rows.FromSqlRaw(statement.Sql);")]
    public void Every_sql_composing_pattern_matches_the_shape_it_names(string call, string sample)
        => Matches(call, sample).ShouldBeTrue();

    /// <summary>
    /// Every allow-listed name must resolve to a file the scan actually reads, so a rename cannot leave a
    /// permission behind that covers nothing while the file that inherited the behaviour goes unguarded.
    /// </summary>
    [Fact]
    public void Every_allow_listed_file_still_exists()
        => _sqlComposingFiles.ShouldBeSubsetOf(SourceFiles().Select(Path.GetFileName));

    /// <summary>
    /// The claim and dispatch statements stay raw SQL rather than LINQ over the context.
    /// </summary>
    /// <remarks>
    /// <c>UseRelationalNulls()</c> is on in both drivers, so a LINQ comparison over a nullable column no
    /// longer means what it means in C# (<c>docs/architecture/data-path.md</c>). Raw SQL carries SQL's
    /// semantics natively; this fact is what stops the next edit from reaching for
    /// <c>Where(entry =&gt; entry.ClaimedAt != stale)</c> and silently changing the predicate's meaning.
    /// </remarks>
    /// <param name="file">The outbox source file the rule covers.</param>
    [Theory]
    [InlineData("OutboxTable.cs")]
    [InlineData("EfCoreOutboxStore.cs")]
    public void The_outbox_claim_is_raw_sql_and_never_linq_over_the_context(string file)
    {
        var source = ReadSource(file);

        foreach (var linq in _linqOverTheContext)
        {
            source.ShouldNotContain(linq, Case.Sensitive, file);
        }
    }

    /// <summary>
    /// The LINQ vocabulary the outbox files may not use — the shapes that would put a nullable comparison
    /// under EF's translation instead of SQL's own semantics.
    /// </summary>
    private static readonly string[] _linqOverTheContext =
        [".Where(", ".FirstOrDefault(", "IQueryable", "db.Rows("];

    /// <summary>The named file's code, with whole-line comments removed.</summary>
    private static string ReadSource(string fileName) => Code(
        SourceFiles().SingleOrDefault(file => Path.GetFileName(file) == fileName)
        ?? throw new FileNotFoundException($"No source file named '{fileName}' is scanned.", fileName));

    /// <summary>The files permitted to compose SQL text or construct a <c>DbCommand</c> inside a data package.</summary>
    private static readonly string[] _sqlComposingFiles =
    [
        "EfAlvoData.cs",
        "EfCoreDescriptorVersionStore.cs",
        "EfCoreOutboxStore.cs",
        "EfCoreRuntimeSchemaWriter.cs",
        "EfCoreSchemaMigrator.cs",
        "IdempotencyTable.cs",
        "OutboxTable.cs",
        "PredicateParameterBinder.cs",
        "RelationalReachability.cs",
        "RelationalSqlBatch.cs",
        "RollupRecompute.cs",
        "SqliteCaseSensitiveLike.cs",
        "SystemSchemaInitializer.cs",
        "VersionRowWriter.cs",
    ];

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
    /// <param name="index">The index into <see cref="BannedCalls"/> of the pattern under test.</param>
    /// <param name="sample">A line the pattern must match.</param>
    [Theory]
    [InlineData(0, "db.Attach(row);")]
    [InlineData(0, "db.AttachRange(rows);")]
    [InlineData(0, "db.Attach<Dictionary<string, object>>(row);")]
    [InlineData(1, "db.Update(row);")]
    [InlineData(1, "db.Update<Dictionary<string, object>>(row);")]
    [InlineData(2, "db.Remove(row);")]
    [InlineData(2, "db.RemoveRange<Dictionary<string, object>>(rows);")]
    [InlineData(3, "db.Entry(row).State = EntityState.Modified;")]
    [InlineData(3, "db.Entry<Dictionary<string, object>>(row);")]
    [InlineData(4, "var row = db.Rows(entity).AsTracking().First();")]
    [InlineData(4, "db.Rows(entity).AsTracking<Dictionary<string, object>>();")]
    [InlineData(4, "db.Rows(entity).AsTracking <Dictionary<string, object>> ();")]
    [InlineData(5, "entry.State = EntityState.Modified;")]
    [InlineData(6, "entry.State = EntityState.Modified;")]
    public void Every_banned_pattern_matches_the_call_it_names(int index, string sample)
        => Matches(Pattern(index), sample).ShouldBeTrue();

    /// <summary>
    /// And must not match the shapes this package legitimately uses, so a banned pattern cannot be satisfied by
    /// being unfalsifiable.
    /// </summary>
    /// <param name="index">The index into <see cref="BannedCalls"/> of the pattern under test.</param>
    /// <param name="sample">A line the pattern must not match.</param>
    [Theory]
    [InlineData(6, "if (entry.State == EntityState.Added)")]
    [InlineData(4, "var rows = db.Rows(entity).AsNoTracking();")]
    [InlineData(1, "await root.ExecuteUpdateAsync(setters, cancellationToken);")]
    [InlineData(1, "var updated = Updates(schema); Publish(updated);")]
    public void No_banned_pattern_matches_a_shape_this_package_uses(int index, string sample)
        => Matches(Pattern(index), sample).ShouldBeFalse();

    private static string Pattern(int index) => _bannedCalls[index];

    private static bool Matches(string pattern, string sample) =>
        Regex.IsMatch(sample, pattern, RegexOptions.None, TimeSpan.FromSeconds(5));

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
