using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Rules;
using NSubstitute;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// Turning a caller into a management level: three independent predicates, the highest match, and the
/// bootstrap administrator above all of them.
/// </summary>
public class ManagementAccessEvaluatorTests
{
    /// <summary>
    /// <b>Independent predicates, not a hierarchy.</b> A caller who matches <c>admin</c> and matches
    /// neither <c>developer</c> nor <c>viewer</c> is an administrator — nothing requires a level to
    /// imply the one below it, and a descriptor may well name three disjoint role sets.
    /// </summary>
    [Fact]
    public void A_caller_matching_only_the_admin_level_is_an_admin()
        => Resolve(Caller("manager"), Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"))
            .ShouldBe(ManagementLevel.Admin);

    [Fact]
    public void A_caller_matching_only_the_viewer_level_is_a_viewer()
        => Resolve(Caller("sales"), Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"))
            .ShouldBe(ManagementLevel.Viewer);

    [Fact]
    public void A_caller_matching_two_levels_gets_the_higher_one()
        => Resolve(Caller("editor", "sales"), Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"))
            .ShouldBe(ManagementLevel.Developer);

    [Fact]
    public void A_caller_matching_no_level_has_none()
        => Resolve(Caller("sales"), Levels(admin: "'manager' in @user.roles"))
            .ShouldBe(ManagementLevel.None);

    [Fact]
    public void A_descriptor_with_no_access_block_admits_nobody()
        => Resolve(Caller("manager"), Levels()).ShouldBe(ManagementLevel.None);

    /// <summary>
    /// <b>Before a descriptor is applied there is no catalogue at all</b>, and that must deny rather
    /// than throw or widen — the same fail-closed direction the policy engine takes for an unprimed
    /// policy catalog.
    /// </summary>
    [Fact]
    public void An_unprimed_host_admits_nobody()
    {
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns((PolicyCatalog?)null);

        ManagementCallers.Evaluator(catalogs).Resolve(Caller("manager")).ShouldBe(ManagementLevel.None);
    }

    /// <summary>
    /// <b>The bootstrap administrator bypasses <c>access</c> entirely</b>, and that is a boundary rather
    /// than a hole: it is infrastructure configuration, never the descriptor, so a descriptor with no
    /// <c>access</c> block still has exactly one person who can fix it.
    /// </summary>
    [Fact]
    public void The_bootstrap_admin_is_an_admin_whatever_the_descriptor_says()
    {
        var caller = new AlvoContext
        {
            User = ManagementCallers.Bootstrap,
            Roles = new HashSet<Role> { Role.Anon },
        };

        Resolve(caller, Levels()).ShouldBe(ManagementLevel.Admin);
        Resolve(caller, Levels(admin: "'manager' in @user.roles")).ShouldBe(ManagementLevel.Admin);
    }

    [Fact]
    public void The_anonymous_caller_is_never_an_admin()
        => Resolve(AlvoContext.Anonymous, Levels(admin: "'manager' in @user.roles"))
            .ShouldBe(ManagementLevel.None);

    /// <summary>
    /// A negated level is evaluated as written, including for a caller who holds nothing — the direction
    /// that catches a gate treating "no roles" as "no answer" instead of as an evaluated <c>true</c>.
    /// </summary>
    [Fact]
    public void A_negated_level_is_evaluated_as_written()
    {
        Resolve(Caller("sales"), Levels(viewer: "!('manager' in @user.roles)"))
            .ShouldBe(ManagementLevel.Viewer);
        Resolve(Caller("manager"), Levels(viewer: "!('manager' in @user.roles)"))
            .ShouldBe(ManagementLevel.None);
    }

    /// <summary>
    /// A level naming two roles admits only a caller holding both — the conjunction is evaluated, not
    /// approximated by "holds any of the named roles".
    /// </summary>
    [Fact]
    public void A_conjunction_requires_every_named_role()
    {
        var levels = Levels(admin: "'manager' in @user.roles && 'editor' in @user.roles");

        Resolve(Caller("manager", "editor"), levels).ShouldBe(ManagementLevel.Admin);
        Resolve(Caller("manager"), levels).ShouldBe(ManagementLevel.None);
    }

    /// <summary>
    /// Every level is evaluated, rather than the search stopping at the first match from the top — the
    /// property that makes "three independent predicates" structural. A caller matching <c>viewer</c>
    /// and <c>admin</c> but not <c>developer</c> still resolves to <c>admin</c>.
    /// </summary>
    [Fact]
    public void A_caller_skipping_the_middle_level_still_reaches_the_top()
        => Resolve(Caller("manager", "sales"), Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"))
            .ShouldBe(ManagementLevel.Admin);

    /// <summary>
    /// <b>Every declared level is evaluated, even once the highest one has matched.</b> This is the one
    /// fact that can see the difference between "evaluate all three, take the highest" and "scan from the
    /// top, return the first match" — the two answer identically for every caller, so a verdict-comparing
    /// fact cannot tell them apart and would leave the rule held by nothing but a comment.
    /// </summary>
    /// <remarks>
    /// The recorded <em>expressions</em> are compared rather than a call count: "each declared level
    /// reached the evaluator" is the claim, and a count says it only by arithmetic that a fourth level
    /// would silently invalidate.
    /// </remarks>
    [Fact]
    public void Every_declared_level_is_evaluated_even_after_the_highest_has_matched()
    {
        var catalog = PolicyCatalogBuilderProbe.Build(Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"));
        var recording = new RecordingEvaluator(new PredicateEvaluator());

        ManagementCallers.Evaluator(ManagementCallers.Holding(catalog), recording)
            .Resolve(Caller("manager", "editor", "sales"))
            .ShouldBe(ManagementLevel.Admin);

        var levels = catalog.ManagementAccess;
        recording.Evaluated.ShouldBe(
            [levels.Viewer!, levels.Developer!, levels.Admin!],
            ignoreOrder: true,
            "a level the gate never evaluates is a level the gate does not enforce");
    }

    /// <summary>
    /// <b>A host's own <see cref="IAlvoBootstrapAdmin"/> cannot lift the anonymous caller.</b> The port is
    /// public, so a host writes the implementation, and the filter maps every unauthenticated management
    /// request onto <see cref="AlvoContext.Anonymous"/> — whose <see cref="UserId"/> is the reserved
    /// all-zero one. An implementation answering <see langword="true"/> for it would turn every anonymous
    /// request into full administration, project deletion included, with no second check anywhere. The
    /// port's doc comment forbids it; this is the gate enforcing it, which is the security-core
    /// checklist's own standard — structural, not merely discouraged by convention.
    /// </summary>
    [Fact]
    public void A_bootstrap_port_that_recognises_everyone_still_does_not_lift_the_anonymous_caller()
    {
        var subject = ManagementCallers.Evaluator(
            ManagementCallers.Primed(Levels()),
            new PredicateEvaluator(),
            ManagementCallers.Bootstrapped(_ => true));

        subject.Resolve(AlvoContext.Anonymous).ShouldBe(ManagementLevel.None);
        subject.Allows(ManagementOperation.DeleteProject, AlvoContext.Anonymous).ShouldBeFalse();
        subject.Resolve(Caller("sales")).ShouldBe(
            ManagementLevel.Admin, "an identified caller the port does recognise is still lifted");
    }

    // ---- Allows, the layer where levels DO form a hierarchy -----------------------------------

    /// <summary>
    /// The predicates are independent; what a resolved level may <em>do</em> is ordered. An admin may do
    /// everything a developer may, because <c>admin</c> is defined as "everything, plus settings".
    /// </summary>
    [Fact]
    public void An_admin_may_do_everything_a_developer_and_a_viewer_may()
    {
        var subject = ManagementCallers.Evaluator(Levels(admin: "'manager' in @user.roles"));
        var caller = Caller("manager");

        foreach (var operation in Enum.GetValues<ManagementOperation>())
        {
            subject.Allows(operation, caller).ShouldBeTrue(operation.ToString());
        }
    }

    [Fact]
    public void A_developer_may_apply_a_descriptor_but_not_reach_settings()
    {
        var subject = ManagementCallers.Evaluator(Levels(developer: "'editor' in @user.roles"));
        var caller = Caller("editor");

        subject.Allows(ManagementOperation.ApplyDescriptor, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.RollbackRevision, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.GetDescriptor, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.ManageApiKeys, caller).ShouldBeFalse();
        subject.Allows(ManagementOperation.ManageUsers, caller).ShouldBeFalse();
        subject.Allows(ManagementOperation.DeleteProject, caller).ShouldBeFalse();
    }

    [Fact]
    public void A_viewer_may_read_and_simulate_but_never_write()
    {
        var subject = ManagementCallers.Evaluator(Levels(viewer: "'sales' in @user.roles"));
        var caller = Caller("sales");

        subject.Allows(ManagementOperation.GetSchema, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.SimulatePolicy, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.ApplyDescriptor, caller).ShouldBeFalse();
        subject.Allows(ManagementOperation.RollbackRevision, caller).ShouldBeFalse();
    }

    /// <summary>
    /// <b>An operation requiring <see cref="ManagementLevel.None"/> is refused, not opened.</b> No table
    /// entry is <c>None</c> today and a fact asserts none ever is — but a plain
    /// <c>Resolve(context) &gt;= required</c> answers <see langword="true"/> for <em>every</em> caller the
    /// moment one is, so a one-word typo in the table would be the difference between an operation and an
    /// open door. The refusal belongs in the code; the completeness fact is then belt-and-braces.
    /// </summary>
    [Fact]
    public void An_operation_with_no_required_level_is_refused_even_for_an_admin()
    {
        var subject = ManagementCallers.Evaluator(Levels(admin: "'manager' in @user.roles"));

        subject.Resolve(Caller("manager")).ShouldBe(ManagementLevel.Admin);
        subject.Allows(ManagementLevel.None, Caller("manager")).ShouldBeFalse(
            "an operation nobody has decided a level for is undecided, and undecided is refused");
        subject.Allows(ManagementLevel.None, AlvoContext.Anonymous).ShouldBeFalse();
    }

    [Fact]
    public void A_caller_matching_no_level_may_do_nothing_at_all()
    {
        var subject = ManagementCallers.Evaluator(Levels(admin: "'manager' in @user.roles"));
        var caller = Caller("sales");

        foreach (var operation in Enum.GetValues<ManagementOperation>())
        {
            subject.Allows(operation, caller).ShouldBeFalse(operation.ToString());
        }
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static AlvoContext Caller(params string[] roleNames) => ManagementCallers.Caller(roleNames);

    private static Access Levels(string? admin = null, string? developer = null, string? viewer = null) =>
        ManagementCallers.Levels(admin, developer, viewer);

    private static ManagementLevel Resolve(AlvoContext caller, Access levels) =>
        ManagementCallers.Evaluator(levels).Resolve(caller);

    /// <summary>The product's own evaluator, recording which predicates were handed to it.</summary>
    /// <param name="inner">The real evaluator every call is delegated to.</param>
    private sealed class RecordingEvaluator(IPredicateEvaluator inner) : IPredicateEvaluator
    {
        private readonly List<CompiledExpression> _evaluated = [];

        /// <summary>Gets every predicate evaluated so far, in the order they arrived.</summary>
        internal IReadOnlyList<CompiledExpression> Evaluated => _evaluated;

        /// <inheritdoc/>
        public bool Evaluate(
            CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context)
        {
            _evaluated.Add(expression);
            return inner.Evaluate(expression, current, previous, context);
        }
    }
}
