using MMLib.Alvo.Admin.Components.Home;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Management.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>The Overview detects every qualified slot the build reports, and no slot the build does not.</b>
/// </summary>
/// <remarks>
/// <para>
/// <c>DeclaredSlots</c> restates the core's "is this key declared" predicates, because they are internal to
/// <c>MMLib.Alvo</c> and the dashboard does not reference it. A top-level block it does not know still reaches the
/// Overview through the "present and not empty" fallback, but a qualified slot (<c>auth.providers</c>,
/// <c>entity.storage</c>, <c>entity.realtime</c>) has no fallback: one added to <c>UnhonouredSubsystems</c> without a
/// reader there would be served and never drawn, which is the silence §8d item 27 closed.
/// </para>
/// <para>
/// This suite is the one that sees both assemblies' internals, so the agreement is asserted here, over the real
/// <c>CapabilityReport</c> rather than a copy of its names — and over <b>meaning</b>, not names alone: for every row
/// the build reports, the core's <c>IsDeclaredBy</c> and the dashboard's <c>DeclaredSlots.Declares</c> give the same
/// answer on every example descriptor and on each case declined by value (B2 review, finding 4).
/// </para>
/// </remarks>
public sealed class DeclaredSlotsAgreementTests
{
    [Fact]
    public void Every_qualified_slot_the_build_reports_is_one_the_overview_detects()
    {
        var reported = CapabilityReport.Project().Warned
            .Select(block => block.Block)
            .Where(block => block.Contains('.', StringComparison.Ordinal))
            .ToList();

        reported.ShouldBe(
            DeclaredSlots.Qualified,
            ignoreOrder: true,
            "a slot the build reports and the Overview cannot detect is never drawn; a slot the Overview detects "
            + "and the build no longer reports is a reader of nothing");
    }

    /// <summary>The descriptors the agreement is measured over: every example, and the cases declined by value.</summary>
    public static TheoryData<string> Descriptors()
    {
        var data = new TheoryData<string>();
        foreach (var example in Directory.GetFiles(Path.Combine(RepositoryRoot.Find(), "examples"), "*.alvo.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            data.Add(File.ReadAllText(example));
        }

        foreach (var body in _declinedOrEdge)
        {
            /* The parser requires entities; a case about another block gets an empty set. */
            var entities = body.Contains("\"entities\"", StringComparison.Ordinal) ? string.Empty : """, "entities": {}""";
            data.Add($$"""{ "apiVersion": "alvo.dev/v1", "name": "agreement", {{body}}{{entities}} }""");
        }

        return data;
    }

    private static readonly string[] _declinedOrEdge =
    [
        """ "dynamicEntities": { "enabled": false, "maxEntitiesPerTenant": 5 }""",
        """ "dynamicEntities": { "maxEntitiesPerTenant": 5 }""",
        """ "dynamicEntities": { "enabled": true }""",
        """ "automation": {}""",
        """ "webhooks": { "endpoints": {} }""",
        """ "functions": {}""",
        """ "templates": {}""",
        """ "auth": { "providers": ["local"] }, "entities": { "n": { "realtime": false, "fields": { "t": { "type": "string" } } } }""",
        """ "auth": { "providers": ["local", "github"] }, "entities": { "n": { "realtime": true, "storage": "dynamic", "fields": { "t": { "type": "string" } } } }""",
        """ "entities": { "n": { "storage": "physical", "fields": { "t": { "type": "string" } } } }""",
    ];

    [Theory]
    [MemberData(nameof(Descriptors))]
    public void The_overview_and_the_build_agree_on_what_is_declared(string json)
    {
        var descriptor = AlvoDescriptor.Parse(json);
        using var document = JsonDocument.Parse(json);

        foreach (var row in UnhonouredSubsystems.All.Concat(UnhonouredSubsystems.WithinBlocks).Concat(UnhonouredSubsystems.ReportedOnly))
        {
            DeclaredSlots.Declares(document.RootElement, row.Block).ShouldBe(
                row.IsDeclaredBy(descriptor),
                $"'{row.Block}': the Overview and the build must agree on whether this descriptor declares it");
        }
    }
}
