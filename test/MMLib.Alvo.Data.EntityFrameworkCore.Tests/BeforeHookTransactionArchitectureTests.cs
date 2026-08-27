using MMLib.Alvo.Testing;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A before-hook runs <b>inside the transaction it guards</b>, and this is where that is asserted, because it
/// is not observable from a write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source scan and not a behavioural fact.</b> A hook runs <em>before</em> the row is written — that
/// is what lets it patch the candidate — so at the moment a <c>reject</c> fires there is nothing yet written
/// for a rollback to undo. Move the pipeline call from inside the transaction to just outside it and every
/// black-box outcome is identical: the refusal still leaves no row, the mutation still reaches the same
/// column, both engines still agree. A behavioural "the refusal left no row" fact would therefore stay green
/// under exactly the change it appears to guard against, which is worse than no fact — it is a claim the suite
/// cannot support. The placement is real and it is structural, so it is pinned structurally.
/// </para>
/// <para>
/// <b>What the placement buys, so the invariant is not cargo.</b> On the update and delete faces the hook
/// needs the <em>row-locked</em> pre-image: <c>old.</c> and <c>changed(...)</c> are answered from it, and a
/// pre-image read outside the transaction could be overwritten between the hook's judgement and the write. On
/// the create face it is what makes the write and everything the hook decided one atomic unit as soon as
/// anything is written before the hook — an outbox row, a version row, a future validation write — which is
/// the change this fact will still be guarding after that day.
/// </para>
/// <para>
/// <b>The enclosing-member list is the second half, and it pins the exclusion the design owed.</b>
/// <c>ReplayableCreateAsync</c> and <c>CreatedOrReplayedAsync</c> must never call the pipeline: the first
/// retries a contended create and the second branches between a fresh write and a replay of a stored one, so a
/// call in either would run a hook for a replay — doubling a <c>mutate</c> over a value already stored, and
/// letting a <c>reject</c> refuse a retry of a create the caller was told succeeded.
/// </para>
/// </remarks>
public class BeforeHookTransactionArchitectureTests
{
    /// <summary>
    /// Every call to the before-hook pipeline sits after the thing that proves a transaction is open: the
    /// <c>BeginTransactionAsync</c> in the same member, or the row-locked pre-image read that can only happen
    /// inside one.
    /// </summary>
    [Fact]
    public void Every_before_hook_call_site_runs_inside_the_writes_own_transaction()
    {
        var outside = CallSites()
            .Where(site => !site.IsInsideATransaction)
            .Select(site => site.Member)
            .ToList();

        outside.ShouldBeEmpty(
            "a before-hook must run inside the transaction it guards, so its call has to come after "
            + $"one of {string.Join(", ", _markers)} in the same member. Offending members: "
            + $"{string.Join(", ", outside)}.");
    }

    /// <summary>
    /// The pipeline is called from exactly the four write bodies, and from no other — in particular not from
    /// the two on the idempotent path that would run a hook for a replay.
    /// </summary>
    [Fact]
    public void The_pipeline_is_called_from_the_four_write_bodies_and_nowhere_else()
        => CallSites().Select(site => site.Member).Distinct(StringComparer.Ordinal).ShouldBe(
            ["CreatedAsync", "RecordedCreateAsync", "WriteAsync", "EraseAsync"],
            ignoreOrder: true,
            "a fifth call site is either a write face that grew one twice or a replay path that must not have "
            + "one at all");

    /// <summary>
    /// The non-vacuity control. The scan is a line-order comparison inside a member, so the way it fails
    /// silently is by finding no call site at all — a renamed helper, a moved file, a changed prefix — and then
    /// reporting an empty offender list as success.
    /// </summary>
    [Fact]
    public void The_scan_finds_the_call_sites_it_is_written_about()
        => CallSites().Count.ShouldBe(4, "one call per write body; a different number means the scan drifted");

    /// <summary>
    /// The other half of the control: the scan can tell the two orderings apart. Handed a member whose
    /// pipeline call precedes the transaction, it reports it — which is the mutation this file exists to catch.
    /// </summary>
    /// <remarks>
    /// <b>Every row that expects <see langword="false"/> for an ordering reason carries a declaration line of
    /// its own.</b> Without one the call lands on line 0, which the scan reads as a member declaration and
    /// skips — so the row answered <see langword="false"/> because no call was found, not because the order was
    /// wrong, and it would have gone on answering <see langword="false"/> however the ordering rule changed.
    /// </remarks>
    [Theory]
    [InlineData("private Task X()\nRunBeforeCreate(db);\nawait using var transaction = await db.Database.BeginTransactionAsync();", false)]
    [InlineData("await using var transaction = await db.Database.BeginTransactionAsync();\nRunBeforeCreate(db);", true)]
    [InlineData("var stored = await SingleAsync(db, PreImageMutation.Update);\nRunBeforeUpdate(db);", true)]
    [InlineData("private Task X()\nRunBeforeUpdate(db);\nvar stored = await SingleAsync(db, PreImageMutation.Update);", false)]
    [InlineData("private Task X(IDbContextTransaction transaction)\nRunBeforeCreate(db);", true)]
    [InlineData("private Task X()\nRunBeforeCreate(db);\nprivate Task X(IDbContextTransaction transaction)", false)]
    [InlineData("await using var transaction = await db.Database.BeginTransactionAsync();\nRunBeforeCreate(db);\nawait transaction.CommitAsync();", true)]
    [InlineData("await using var transaction = await db.Database.BeginTransactionAsync();\nRunBeforeCreate(db);\nawait transaction.CommitAsync();\nRunBeforeCreate(db);", false)]
    [InlineData("await using var transaction = await db.Database.BeginTransactionAsync();\nawait transaction.RollbackAsync();\nRunBeforeCreate(db);", false)]
    public void The_scan_reads_the_order_within_a_member(string body, bool expected)
        => IsGuarded(body).ShouldBe(expected);

    /// <summary>The helper prefix every pipeline call goes through, so a call site is greppable at all.</summary>
    private const string PipelineCall = "RunBefore";

    private const string TransactionOpened = "BeginTransactionAsync";

    /// <summary>
    /// The row-locked pre-image read. It stands in for "a transaction is open" on the update and delete bodies,
    /// which are called from inside one and do not open it themselves — and it is the stronger statement of the
    /// two, because the lock is the reason those hooks need to be there.
    /// </summary>
    private const string PreImageRead = "PreImageMutation.";

    /// <summary>
    /// A transaction handed in as a parameter. The idempotent create's body does not open one and does not read
    /// a locked pre-image — it is called from the method that opened the transaction and receives it — and a
    /// member that <em>holds</em> one is inside one by construction, which is the strongest of the three
    /// markers rather than the weakest. It is deliberately checked only <em>before</em> the call, like the other
    /// two, so it cannot be satisfied by a signature the call precedes.
    /// </summary>
    private const string TransactionHeld = "IDbContextTransaction";

    /// <summary>One member that calls the pipeline, and whether its call is guarded by a transaction.</summary>
    /// <param name="Member">The member's name, for the failure message.</param>
    /// <param name="IsInsideATransaction">Whether the call comes after the transaction or the locked pre-image.</param>
    private sealed record CallSite(string Member, bool IsInsideATransaction);

    private static IReadOnlyList<CallSite> CallSites() =>
        [.. Members().SelectMany(member =>
            Calls(member.Body).Select(inside => new CallSite(member.Name, inside)))];

    /// <summary>
    /// One answer per pipeline call in <paramref name="body"/>, in source order — never one per member.
    /// </summary>
    /// <remarks>
    /// <b>It used to be one per member, judged on the <em>first</em> call, and that left a hole wide enough to
    /// walk a hook out of its transaction through.</b> A body that kept a correct first call and grew a second
    /// one after <c>CommitAsync</c> satisfied all three facts: the member reported guarded, the member list was
    /// unchanged, and the count still said four. A hook could then run outside its write transaction, or twice,
    /// with the suite green. Per call, that body reports two sites and one of them is outside — and the count
    /// fact becomes a real ceiling rather than only a floor.
    /// </remarks>
    /// <remarks>
    /// The scan starts at line 1 because line 0 is the member's own declaration: a mention there is the
    /// pipeline helper declaring itself, not a call, which is why the helper contributes no call site.
    /// </remarks>
    private static IEnumerable<bool> Calls(string body)
    {
        var lines = body.Split('\n');

        for (var index = 1; index < lines.Length; index++)
        {
            if (lines[index].Contains(PipelineCall, StringComparison.Ordinal))
            {
                yield return IsInsideATransaction(lines, index);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="body"/> calls the pipeline at all and every one of those calls is inside a
    /// transaction. "No call at all" is not guarded: it is the way this scan fails silently.
    /// </summary>
    private static bool IsGuarded(string body)
    {
        var calls = Calls(body).ToList();

        return calls.Count > 0 && calls.TrueForAll(inside => inside);
    }

    /// <summary>
    /// Whether the <b>nearest</b> transaction event above <paramref name="call"/> opens one rather than ends
    /// it.
    /// </summary>
    /// <remarks>
    /// Nearest-wins rather than "a marker appears somewhere above", because the second reading is satisfied
    /// for the whole rest of a member by an opener that has already been committed away — which is exactly the
    /// shape of a call added after <c>CommitAsync</c>.
    /// </remarks>
    private static bool IsInsideATransaction(string[] lines, int call)
    {
        for (var index = call - 1; index >= 0; index--)
        {
            if (_closers.Any(closer => lines[index].Contains(closer, StringComparison.Ordinal)))
            {
                return false;
            }

            if (_markers.Any(marker => lines[index].Contains(marker, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Everything whose presence before the call proves a transaction is open around it.</summary>
    private static readonly string[] _markers = [TransactionOpened, PreImageRead, TransactionHeld];

    /// <summary>And everything that <em>ends</em> the transaction a marker above it opened.</summary>
    private static readonly string[] _closers = ["CommitAsync", "RollbackAsync"];

    /// <summary>One member of <c>EfAlvoData</c>: its name and its body, comments stripped.</summary>
    /// <param name="Name">The member's name.</param>
    /// <param name="Body">Its source, from the declaration line to the next member's.</param>
    private sealed record Member(string Name, string Body);

    /// <summary>
    /// <c>EfAlvoData</c>'s members, split on the declaration lines at class indentation. Crude on purpose: the
    /// question is line order inside one member, and a parser would be a second thing to be wrong. Comments are
    /// stripped first, so the remarks that <em>discuss</em> the placement — and exist to explain it — are not
    /// mistaken for calls.
    /// </summary>
    private static List<Member> Members()
    {
        var members = new List<Member>();
        foreach (var line in Code().Split('\n'))
        {
            if (DeclaredName(line) is { } name)
            {
                members.Add(new Member(name, line));
            }
            else if (members.Count > 0)
            {
                members[^1] = members[^1] with { Body = $"{members[^1].Body}\n{line}" };
            }
        }

        return members;
    }

    /// <summary>
    /// The member name a class-indented declaration line introduces, or <see langword="null"/> when the line is
    /// not one. Matched on the four-space indent plus an accessibility keyword, which is every member
    /// declaration in this file and nothing inside a body.
    /// </summary>
    /// <remarks>
    /// The name is the identifier before the parameter list rather than the last word before the first
    /// <c>(</c>, because a return type may itself contain one: <c>Task&lt;(AlvoRecord, …)&gt; WriteAsync(</c>
    /// would otherwise be read as a member called <c>Task</c> — which it was, and the enclosing-member fact
    /// caught it.
    /// </remarks>
    private static string? DeclaredName(string line)
    {
        if (!_accessibilities.Any(keyword => line.StartsWith($"    {keyword} ", StringComparison.Ordinal)))
        {
            return null;
        }

        var call = Regex.Match(line, @"(\w+)\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5));
        if (call.Success)
        {
            return call.Groups[1].Value;
        }

        var words = line.Split([' ', '<', '{'], StringSplitOptions.RemoveEmptyEntries);

        return words.Length == 0 ? null : words[^1];
    }

    private static readonly string[] _accessibilities = ["private", "public", "internal", "protected"];

    private static string Code() => string.Join(
        '\n',
        File.ReadAllLines(Path.Combine(
                RepositoryRoot.Find(), "src", "MMLib.Alvo.Data.EntityFrameworkCore", "Internal", "EfAlvoData.cs"))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
