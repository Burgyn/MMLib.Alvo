using MMLib.Alvo.Admin.Components.Layout;
using System.Reflection;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// The package boundary the whole F5 design rests on.
/// </summary>
/// <remarks>
/// <para>
/// Design §1.2: <b><c>MMLib.Alvo.Admin</c> must not hold a reference to <c>MMLib.Alvo</c>.</b> That
/// is what turns <i>"the dashboard and the CLI are clients of the same API"</i> from a promise into
/// a structural fact — a dashboard that could reach into the core would, sooner or later, reach
/// past the Management API and grow a capability no other client has, and the claim would then be
/// false in a way no reviewer could see from a diff.
/// </para>
/// <para>
/// <b>It is checked against the loaded assembly's references rather than the project file</b>,
/// because a project file names what was asked for and an assembly names what was linked — a
/// transitive reference that arrived through a third project would satisfy the first check and
/// break the rule.
/// </para>
/// </remarks>
public sealed class BoundaryArchitectureTests
{
    private static readonly AssemblyName[] _references =
        typeof(AlvoAdmin).Assembly.GetReferencedAssemblies();

    [Fact]
    public void The_dashboard_does_not_reference_the_core()
        => _references
            .Select(reference => reference.Name)
            .ShouldNotContain("MMLib.Alvo");

    [Fact]
    public void The_dashboard_reaches_the_core_only_through_the_abstractions()
        => _references
            .Select(reference => reference.Name)
            .ShouldContain("MMLib.Alvo.Abstractions");

    /// <summary>
    /// The dashboard must not acquire the identity package either.
    /// </summary>
    /// <remarks>
    /// Not named by §1.2, and it follows from the same argument: the identity package carries EF
    /// Core and ASP.NET Core Identity, and a dashboard that referenced it would be one that could
    /// only ever be hosted beside <em>that</em> membership store. The conversion it would need is
    /// <see cref="IAlvoAdminCallerResolver"/>, which the host fills.
    /// </remarks>
    [Fact]
    public void The_dashboard_does_not_reference_the_identity_package()
        => _references
            .Select(reference => reference.Name)
            .ShouldNotContain("MMLib.Alvo.Identity");

    /// <summary>
    /// The phone's bottom bar carries five live sections and no <c>Not yet</c> one.
    /// </summary>
    /// <remarks>
    /// Design §4.3 makes this an acceptance criterion rather than a layout preference: a bar of
    /// about five items is the whole navigation at 375 px, so an entry in it that leads to
    /// something which does not work is a navigation that lies — and the alternative, deciding the
    /// phone's navigation separately, is a second navigation to keep in step.
    /// </remarks>
    [Fact]
    public void The_phone_bar_carries_five_sections_that_work()
    {
        AdminNavigation.Bar.Count().ShouldBe(5);
        AdminNavigation.Bar.ShouldAllBe(entry => !entry.NotYet);
    }

    /// <summary>
    /// Every section's <c>g</c> shortcut is unique, and no <c>Not yet</c> section has one.
    /// </summary>
    /// <remarks>
    /// A duplicate would make <c>g</c> plus a letter go wherever the list happened to be ordered,
    /// which is the kind of defect that survives every review because nobody reads a table for
    /// collisions.
    /// </remarks>
    [Fact]
    public void Every_shortcut_is_unique_and_leads_somewhere_that_works()
    {
        var keys = AdminNavigation.All
            .Where(entry => entry.GoKey is not null)
            .Select(entry => entry.GoKey!.Value)
            .ToList();

        keys.Distinct().Count().ShouldBe(keys.Count);
        AdminNavigation.All.Where(entry => entry.NotYet).ShouldAllBe(entry => entry.GoKey == null);
    }

    /// <summary>
    /// Every navigation path sits under the dashboard's base path.
    /// </summary>
    [Fact]
    public void Every_section_is_inside_the_dashboard()
        => AdminNavigation.All.ShouldAllBe(entry => entry.Path.StartsWith(AlvoAdmin.BasePath, StringComparison.Ordinal));
}
