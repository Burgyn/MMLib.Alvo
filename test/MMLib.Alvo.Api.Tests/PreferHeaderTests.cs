using Microsoft.Extensions.Primitives;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// RFC 7240's <c>Prefer</c> grammar, for the one preference Alvo reads. The header is a list, its values may
/// be quoted, and a preference may carry parameters — so a naive split answers wrongly on inputs a real
/// client or intermediary produces.
/// </summary>
public sealed class PreferHeaderTests
{
    /// <summary>Each of RFC 7240's three count spellings maps to its own preference.</summary>
    /// <remarks>
    /// The expectation is the enum member's <em>name</em> rather than the member: <c>CountPreference</c> is
    /// <see langword="internal"/> and an xUnit test method has to be public, so an internal parameter type
    /// would not compile. Comparing names still fails if a spelling is mapped to the wrong member.
    /// </remarks>
    [Theory]
    [InlineData("count=exact", nameof(CountPreference.Exact))]
    [InlineData("count=planned", nameof(CountPreference.Planned))]
    [InlineData("count=estimated", nameof(CountPreference.Estimated))]
    public void The_three_spellings_are_recognised(string header, string expected)
        => PreferHeader.Count(header).ShouldNotBeNull().ToString().ShouldBe(expected);

    /// <summary>
    /// A token is case-insensitive and RFC 7240's <c>word</c> may be a quoted-string, so both spellings of the
    /// same preference mean the same thing.
    /// </summary>
    [Theory]
    [InlineData("COUNT=EXACT")]
    [InlineData("count=\"exact\"")]
    [InlineData("  count = exact  ")]
    public void A_preference_is_read_case_insensitively_and_unquoted(string header)
        => PreferHeader.Count(header).ShouldBe(CountPreference.Exact);

    /// <summary>
    /// The header is a comma-separated list and a preference may carry <c>;</c>-delimited parameters. Both
    /// have to be handled, or a `count` sitting beside something else — which is what an intermediary adding
    /// its own preference produces — is missed.
    /// </summary>
    [Theory]
    [InlineData("respond-async, count=exact")]
    [InlineData("count=exact, return=representation")]
    [InlineData("count=exact; foo=bar")]
    [InlineData("wait=100; handling=lenient, count=exact")]
    public void A_count_beside_other_preferences_is_still_read(string header)
        => PreferHeader.Count(header).ShouldBe(CountPreference.Exact);

    /// <summary>
    /// A quoted value may contain a comma, so the list is scanned rather than split — otherwise the second
    /// half of one preference reads as a preference of its own.
    /// </summary>
    [Fact]
    public void A_comma_inside_a_quoted_value_does_not_split_the_list()
        => PreferHeader.Count("handling=\"a,b\", count=exact").ShouldBe(CountPreference.Exact);

    /// <summary>
    /// <b>An unrecognised preference — or an unrecognised value for a recognised one — is ignored, not
    /// refused.</b> RFC 7240 §2 requires exactly that, and <c>Preference-Applied</c> is where the client
    /// learns nothing was applied. It is the one deliberate departure from this API's "refuse, never ignore"
    /// rule, so it is asserted rather than left as a property of the code.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("respond-async")]
    [InlineData("count")]
    [InlineData("count=exakt")]
    [InlineData("count=")]
    [InlineData("counts=exact")]
    [InlineData("=exact")]
    public void Anything_this_server_does_not_recognise_is_ignored(string header)
        => PreferHeader.Count(header).ShouldBeNull();

    /// <summary>
    /// The header may arrive more than once, and RFC 7240 §2 makes the <b>first</b> occurrence of a repeated
    /// preference the one that applies — so a value an intermediary appended cannot override the client's.
    /// </summary>
    [Fact]
    public void The_first_recognised_count_wins_when_the_header_repeats_it()
        => PreferHeader.Count(new StringValues(["count=planned", "count=exact"]))
            .ShouldBe(CountPreference.Planned);

    [Fact]
    public void An_absent_header_asks_for_nothing()
        => PreferHeader.Count(StringValues.Empty).ShouldBeNull();
}
