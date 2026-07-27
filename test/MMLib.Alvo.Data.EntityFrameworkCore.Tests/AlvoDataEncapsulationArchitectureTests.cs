using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MMLib.Alvo.Tests.Architecture;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The invariant the de-risking spike said a reviewer must check, as a test instead of a convention: EF's
/// change tracker must be unreachable from outside the data path. The family-wide half is
/// <c>SharedArchitectureRules.No_public_surface_exposes_efs_context_or_change_tracker</c>, which runs against
/// every Alvo assembly; this file carries the two things only a project that can see EF can assert — that the
/// shared scan's forbidden names are real types, and that the scan actually bites.
/// </summary>
public class AlvoDataEncapsulationArchitectureTests
{
    /// <summary>
    /// The context is not merely undocumented, it is unnameable: <see langword="internal"/> so no host can
    /// declare a variable of its type, and <see langword="sealed"/> so no host can derive one.
    /// </summary>
    [Fact]
    public void The_data_context_is_internal_and_sealed()
    {
        var context = typeof(AlvoDataContextFactory).Assembly.GetType(
            "MMLib.Alvo.Data.EntityFrameworkCore.AlvoDataContext", throwOnError: true)!;

        context.IsPublic.ShouldBeFalse();
        context.IsSealed.ShouldBeTrue();
        context.IsSubclassOf(typeof(DbContext)).ShouldBeTrue();
    }

    /// <summary>
    /// The shared scan matches EF's types by full name because it is linked into projects that cannot
    /// reference EF. This is the compensating fact: if EF ever renames one of them, the rule would go quietly
    /// vacuous everywhere, and this is the one project that can notice.
    /// </summary>
    [Fact]
    public void Every_forbidden_type_name_still_resolves_to_a_real_ef_type()
    {
        var resolved = EfSurfaceScan.ForbiddenTypeNames
            .Select(name => typeof(DbContext).Assembly.GetType(name))
            .ToList();

        resolved.ShouldNotContain((Type?)null);
        resolved.ShouldContain(typeof(DbContext));
        resolved.ShouldContain(typeof(ChangeTracker));
        resolved.ShouldContain(typeof(DbSet<>));
    }

    /// <summary>
    /// The positive control. An assembly with nothing to hide passes the scan trivially, so the only way to
    /// know the scan can fail is to hand it something that must fail — one shape per way a context can leak:
    /// a return value, a parameter, a nested generic, an array, and inheritance.
    /// </summary>
    /// <param name="offender">The deliberately offending type.</param>
    [Theory]
    [InlineData(typeof(ReturnsAContext))]
    [InlineData(typeof(TakesAChangeTracker))]
    [InlineData(typeof(ReturnsASetInsideATask))]
    [InlineData(typeof(ReturnsAnArrayOfContexts))]
    [InlineData(typeof(DerivesFromAContext))]
    [InlineData(typeof(ExposesAContextToSubclasses))]
    public void The_scan_reports_every_way_a_context_can_reach_a_caller(Type offender)
        => EfSurfaceScan.Offenders([offender]).ShouldNotBeEmpty();

    /// <summary>The negative control: a type with no EF in its surface is not reported.</summary>
    [Fact]
    public void The_scan_reports_nothing_for_a_type_that_hides_its_context()
        => EfSurfaceScan.Offenders([typeof(HidesItsContext)]).ShouldBeEmpty();

    public sealed class ReturnsAContext
    {
        public static DbContext Context => null!;
    }

    public sealed class TakesAChangeTracker
    {
        public static void Observe(ChangeTracker tracker) => _ = tracker;
    }

    public sealed class ReturnsASetInsideATask
    {
        public static Task<DbSet<HidesItsContext>> RowsAsync() => throw new NotSupportedException();
    }

    public sealed class ReturnsAnArrayOfContexts
    {
        public static DbContext[] Contexts => [];
    }

    public sealed class DerivesFromAContext : DbContext;

    public class ExposesAContextToSubclasses
    {
        protected static DbContext Context => null!;
    }

    public sealed class HidesItsContext
    {
        public static bool HasContext => Context() is not null;

        private static DbContext? Context() => null;
    }
}
