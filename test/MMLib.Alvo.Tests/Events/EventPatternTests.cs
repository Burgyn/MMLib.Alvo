using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Testing;

using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The frozen <c>$defs/eventPattern</c> grammar's two readable properties: which namespaces it reserves
/// (<see cref="AlvoEventName"/>, in <c>Abstractions</c>, because that is the wire contract) and whether one
/// pattern subscribes with a wildcard (<see cref="EventPattern"/>, in the core, because that is the
/// descriptor contract).
/// </summary>
/// <remarks>
/// Both are read by rules that look unrelated — the apply-time wildcard refusal and the guard on a host's own
/// <c>Publish</c> — which is exactly why they must not be two hand-copied alternations. The first fact here
/// ties the set to the schema file itself, so the schema stays the authority over the authority.
/// </remarks>
public sealed class EventPatternTests
{
    /// <summary>
    /// <b>The reserved set is the schema's own first alternation, not a copy of it.</b>
    /// </summary>
    /// <remarks>
    /// Read out of <c>schema/project.schema.json</c> at run time rather than restated, on
    /// <c>UnhonouredSubsystemsTests.Every_unhonoured_subsystem_names_a_block_the_schema_declares</c>'
    /// precedent: a hand-written list that stops matching the schema does not fail, it silently reserves the
    /// wrong names — and here that would mean a namespace a host may mint events into while descriptor rules
    /// can still subscribe to it.
    /// </remarks>
    [Fact]
    public void The_reserved_namespaces_are_the_schema_s_own()
        => AlvoEventName.ReservedNamespaces.Order(StringComparer.Ordinal).ToList().ShouldBe(
            NamespacesTheSchemaAdmits(),
            customMessage:
                "The namespaces a host is refused must be exactly the ones $defs/eventPattern admits, or a "
                + "host could mint events into a namespace descriptor rules can subscribe to");

    [Theory]
    [InlineData("entity.orders.*")]
    [InlineData("entity.*.created")]
    [InlineData("entity.*.*")]
    [InlineData("entity.orders.*.batch")]
    public void A_pattern_with_a_wildcard_segment_is_a_wildcard(string pattern)
        => EventPattern.HasWildcard(pattern).ShouldBeTrue();

    [Theory]
    [InlineData("entity.deals.updated")]
    [InlineData("entity.companies.created.batch")]
    [InlineData("auth.user.login")]
    public void A_pattern_that_names_every_segment_is_not_a_wildcard(string pattern)
        => EventPattern.HasWildcard(pattern).ShouldBeFalse();

    /// <summary>
    /// A <c>*</c> that is only part of a segment is not a wildcard segment.
    /// </summary>
    /// <remarks>
    /// The grammar admits <c>*</c> as a whole segment and nowhere else, so scanning the string for the
    /// character would answer a different question than the grammar asks. Pinned because the cheap
    /// implementation — <c>pattern.Contains('*')</c> — passes every other fact here.
    /// </remarks>
    [Fact]
    public void A_star_inside_a_segment_is_not_a_wildcard_segment()
        => EventPattern.HasWildcard("entity.ord*ers.created").ShouldBeFalse();

    [Theory]
    [InlineData("entity", true)]
    [InlineData("auth", true)]
    [InlineData("storage", true)]
    [InlineData("orders", false)]
    [InlineData("Entity", false)]
    public void Only_the_frameworks_own_namespaces_are_reserved(string segment, bool reserved)
        => AlvoEventName.IsReservedNamespace(segment).ShouldBe(reserved);

    /// <summary>The alternation <c>$defs/eventPattern</c>'s first group spells, read from the schema file.</summary>
    private static IReadOnlyList<string> NamespacesTheSchemaAdmits()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")));

        var pattern = schema.RootElement
            .GetProperty("$defs").GetProperty("eventPattern").GetProperty("pattern").GetString()!;

        var alternation = Regex.Match(pattern, @"^\^\(([a-z|]+)\)\\\.", RegexOptions.None, MatchTimeout);
        alternation.Success.ShouldBeTrue(
            "$defs/eventPattern no longer starts with a namespace alternation, so this fact cannot read one "
            + $"out of it any more: {pattern}");

        return [.. alternation.Groups[1].Value.Split('|').Order(StringComparer.Ordinal)];
    }

    private static TimeSpan MatchTimeout => TimeSpan.FromMilliseconds(100);
}
