using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Admin.Internal;
using System.Reflection;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Every address the dashboard links to reaches the screen it names, and no route shadows another.
/// </summary>
/// <remarks>
/// <b>Read from the route table the router itself reads.</b> An <c>@page</c> directive has to be a literal, so
/// <see cref="AdminPaths"/> cannot be what the directive is written from; it is pinned against the
/// <see cref="RouteAttribute"/>s the Razor compiler emits instead, which is the same guarantee from the other
/// side: a page moved without its address, or an address with no page behind it, fails here rather than in a
/// browser.
/// </remarks>
public class AdminPathsTests
{
    private static readonly Guid _record = Guid.Parse("0b4c3f59-6a0e-4b8e-9d57-0a6f3a8c2d11");

    /// <summary>Each address, with the one screen it is meant to reach.</summary>
    public static TheoryData<string, Type> Addresses => new()
    {
        { AdminPaths.Overview, typeof(Components.Home.Overview) },
        { AdminPaths.Welcome, typeof(Components.Home.Welcome) },
        { AdminPaths.Schema, typeof(Components.Schema.SchemaList) },
        { AdminPaths.Entity("work_orders"), typeof(Components.Schema.Entity) },
        { AdminPaths.Entity("work_orders", EntityTabs.FromSlug(EntityTabs.Rules)), typeof(Components.Schema.Entity) },
        /* F-12: the two names that used to be screens are entities like any other. */
        { AdminPaths.Entity("preview"), typeof(Components.Schema.Entity) },
        { AdminPaths.Entity("transfer"), typeof(Components.Schema.Entity) },
        { AdminPaths.Changes, typeof(Components.Schema.Preview) },
        { AdminPaths.Transfer, typeof(Components.Schema.Transfer) },
        { AdminPaths.Data, typeof(Components.Data.DataList) },
        { AdminPaths.Records("work_orders"), typeof(Components.Data.EntityData) },
        { AdminPaths.Records("work_orders", _record), typeof(Components.Data.EntityData) },
        { AdminPaths.Rules(), typeof(Components.Rules.Rules) },
        { AdminPaths.Rules("work_orders"), typeof(Components.Rules.Rules) },
        { AdminPaths.Access, typeof(Components.Access.Access) },
        { AdminPaths.History, typeof(Components.History.History) },
        { AdminPaths.Integrations, typeof(Components.Integrations.Integrations) },
        { AdminPaths.Automations, typeof(Components.Shell.NotYet) },
        { AdminPaths.Functions, typeof(Components.Shell.NotYet) },
        { AdminPaths.Settings, typeof(Components.Settings.Settings) },
        { AdminPaths.SignIn, typeof(Components.Shell.SignIn) },
    };

    [Theory]
    [MemberData(nameof(Addresses))]
    public void Each_address_reaches_its_screen_and_no_other(string address, Type page)
        => Routes().Where(route => Matches(route.Template, address))
            .Select(route => route.Page).Distinct().ShouldBe([page]);

    [Fact]
    public void Every_routed_template_is_reached_by_an_address()
    {
        var addresses = Addresses.Select(row => row.Data.Item1).ToList();

        Routes().Where(route => !addresses.Any(address => Matches(route.Template, address)))
            .Select(route => route.Template).ShouldBeEmpty();
    }

    /// <summary>
    /// A literal segment where another template takes a parameter is an entity the router can never reach.
    /// </summary>
    [Fact]
    public void No_literal_template_shadows_a_parameter()
    {
        var templates = Routes().Select(route => route.Template).Distinct().ToList();

        templates.SelectMany(literal => templates.Where(other => Shadows(literal, other))
                .Select(other => $"{literal} shadows {other}"))
            .ShouldBeEmpty();
    }

    [Fact]
    public void An_entity_address_carries_its_tab_and_a_records_address_its_record()
    {
        EntityTabs.FromUri($"https://host{AdminPaths.Entity("work_orders", EntityTabs.FromSlug(EntityTabs.OnWrite))}")
            .Slug.ShouldBe(EntityTabs.OnWrite);
        AdminPaths.Records("work_orders", _record).ShouldBe($"/admin/data/work_orders?record={_record}");
    }

    [Theory]
    [InlineData("https://host/admin/changes", true)]
    [InlineData("https://host/admin/changes/?x=1", true)]
    [InlineData("https://host/base/admin/Changes", true)]
    [InlineData("https://host/admin/schema/changes", false)]
    [InlineData("https://host/admin/changes/more", false)]
    public void An_address_is_at_a_path_whatever_its_query(string uri, bool at)
        => AdminPaths.IsAt(uri, AdminPaths.Changes).ShouldBe(at);

    private static IEnumerable<(Type Page, string Template)> Routes() =>
        typeof(AlvoAdmin).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>(inherit: false)
                .Select(route => (type, route.Template)));

    private static bool Matches(string template, string address)
    {
        var path = Segments(address.Split('?')[0]);
        var pattern = Segments(template);

        return path.Length == pattern.Length
            && pattern.Zip(path).All(pair => IsParameter(pair.First)
                || string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="literal"/> takes, with a literal segment, an address <paramref name="other"/>
    /// would take with a parameter — and agrees with it everywhere else.
    /// </summary>
    private static bool Shadows(string literal, string other)
    {
        var first = Segments(literal);
        var second = Segments(other);
        if (first.Length != second.Length)
        {
            return false;
        }

        var pairs = first.Zip(second).ToList();
        return pairs.Any(pair => !IsParameter(pair.First) && IsParameter(pair.Second))
            && pairs.All(pair => IsParameter(pair.Second)
                || string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsParameter(string segment) => segment.StartsWith('{');
}
