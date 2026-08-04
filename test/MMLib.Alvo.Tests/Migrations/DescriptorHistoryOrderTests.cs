using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// Where a booting descriptor sits in a project's applied history — the ordering that decides whether a
/// replica may rewrite the schema another replica just wrote (#145).
/// </summary>
/// <remarks>
/// The two directions carry very different costs and both are pinned here. A missed "older" is the defect
/// itself: a schema that oscillates between two deployed descriptors with no signal. A false "older" is
/// strictly worse — it would stand every ordinary forward deploy down, which is why
/// <see cref="A_descriptor_the_history_has_never_seen_is_a_forward_deploy_not_an_older_pod"/> exists and why
/// the declared-revision override is deliberately one-directional.
/// </remarks>
public sealed class DescriptorHistoryOrderTests
{
    private const string City = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "depots",
          "entities": {
            "depots": { "fields": { "city": { "type": "string" } } }
          }
        }
        """;

    private const string Town = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "depots",
          "entities": {
            "depots": { "fields": { "town": { "type": "string" } } }
          }
        }
        """;

    private const string Region = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "depots",
          "entities": {
            "depots": { "fields": { "region": { "type": "string" } } }
          }
        }
        """;

    [Fact]
    public void An_empty_history_is_a_first_deployment_not_an_older_pod()
        => Check(City, []).ShouldBeNull();

    [Fact]
    public void The_descriptor_the_database_is_on_is_current()
        => Check(City, [City]).ShouldBeNull();

    /// <summary>
    /// The mechanism: this descriptor had its turn, something else has been applied since, so this process is
    /// behind the database.
    /// </summary>
    /// <remarks>
    /// Both revisions are asserted in the message because "you are older" without them is the same unactionable
    /// sentence the destructive refusal already was. The operator has to know which artifact to deploy.
    /// </remarks>
    [Fact]
    public void A_descriptor_recorded_at_an_older_revision_is_an_older_pod()
    {
        var outOfOrder = Check(City, [City, Town]).ShouldNotBeNull();

        outOfOrder.Headline.ShouldContain("revision 1");
        outOfOrder.Headline.ShouldContain("revision 2");
        outOfOrder.Fixes.ShouldNotBeEmpty();
    }

    /// <summary>
    /// The fact that stops this from bricking every deployment: a descriptor nobody has applied here is a
    /// forward deploy, whatever else the history holds.
    /// </summary>
    /// <remarks>
    /// This is the assertion the whole gate lives or dies on. A comparison that answered "older" for an unknown
    /// descriptor — by falling back to "my content is not the current one", say — would stand down every deploy
    /// that has ever changed anything, i.e. all of them.
    /// </remarks>
    [Fact]
    public void A_descriptor_the_history_has_never_seen_is_a_forward_deploy_not_an_older_pod()
        => Check(Region, [City, Town]).ShouldBeNull();

    /// <summary>
    /// Only the <em>newest</em> occurrence decides: a descriptor re-applied since is current, even though an
    /// older revision also records it.
    /// </summary>
    /// <remarks>
    /// Reachable through <c>RuntimeSchemaService</c>, which appends a rollback as a new revision carrying the
    /// restored descriptor. A search that stopped at the first (oldest) match would stand down the pod holding
    /// exactly what the operator just rolled the database back to.
    /// </remarks>
    [Fact]
    public void A_descriptor_re_applied_since_is_current_not_older()
        => Check(City, [City, Town, City]).ShouldBeNull();

    /// <summary>
    /// The comparison is over canonical content, so reformatting a descriptor does not make it a new one.
    /// </summary>
    /// <remarks>
    /// <c>AlvoDescriptor</c> guarantees semantic round-trip fidelity and explicitly not byte identity, so a
    /// reindented or reordered descriptor is the same descriptor. A raw-bytes comparison would call this pod a
    /// forward deploy and let it apply its older schema — the exact hole the gate exists to close, reachable by
    /// nothing more than a different formatter.
    /// </remarks>
    [Fact]
    public void The_comparison_is_canonical_so_reformatting_a_descriptor_does_not_make_it_new()
    {
        const string reformatted =
            "{\"entities\":{\"depots\":{\"fields\":{\"city\":{\"type\":\"string\"}}}},"
            + "\"name\":\"depots\",\"apiVersion\":\"alvo.dev/v1\"}";

        Check(reformatted, [City, Town]).ShouldNotBeNull().Headline.ShouldContain("revision 1");
    }

    /// <summary>
    /// The declared-<c>revision</c> override: a descriptor that says it is an earlier generation than the
    /// applied one is older, even if its content has never been applied here.
    /// </summary>
    /// <remarks>
    /// This is what the counter buys over the history alone, and the only thing it buys: an artifact that
    /// <em>was never deployed</em> but is known to precede what is running — the shape of a rollback to a
    /// branch, or of two environments sharing one database.
    /// </remarks>
    [Fact]
    public void A_lower_declared_revision_is_an_older_pod_even_when_the_history_has_not_seen_it()
    {
        var outOfOrder = Check(Declaring(Region, 4), [Declaring(Town, 9)]).ShouldNotBeNull();

        outOfOrder.Headline.ShouldContain("revision 4");
        outOfOrder.Headline.ShouldContain("revision 9");
    }

    /// <summary>
    /// The override is one-directional: a <em>higher</em> declared revision does not wave a descriptor the
    /// history calls older through.
    /// </summary>
    /// <remarks>
    /// Otherwise the counter is the way <em>around</em> the mechanism rather than an addition to it — bump the
    /// number, redeploy yesterday's schema. The deliberate escape hatch is a bump that changes the descriptor's
    /// canonical content, which makes it a genuinely new artifact the history has never seen, and it still has
    /// to clear the destructive gate.
    /// </remarks>
    [Fact]
    public void A_higher_declared_revision_does_not_wave_a_descriptor_the_history_calls_older_through()
    {
        var older = Declaring(City, 9);

        Check(older, [older, Declaring(Town, 2)]).ShouldNotBeNull()
            .Headline.ShouldContain("already applied");
    }

    /// <summary>
    /// Equal declared revisions with different content are an authoring error and are deliberately
    /// <em>not</em> refused.
    /// </summary>
    /// <remarks>
    /// Two artifacts claiming one generation is genuinely wrong, and refusing it would break the ordinary
    /// edit-and-restart loop for every descriptor carrying a decorative <c>revision</c> its author never bumps —
    /// a field that until now was parsed and read by nothing. The override may only ever add a refusal the
    /// history would have missed, never invent one for a static counter.
    /// </remarks>
    [Fact]
    public void Equal_declared_revisions_with_different_content_are_not_treated_as_out_of_order()
        => Check(Declaring(Region, 3), [Declaring(Town, 3)]).ShouldBeNull();

    /// <summary>
    /// A declared revision on only one side falls through to the history, rather than being read as an ordering.
    /// </summary>
    /// <remarks>
    /// A descriptor that starts declaring a counter mid-life would otherwise be compared against nothing, and
    /// whichever way that comparison defaulted would be a guess. The history is not a guess.
    /// </remarks>
    [Fact]
    public void A_declared_revision_on_one_side_only_falls_through_to_the_history()
    {
        Check(Declaring(Region, 1), [Town]).ShouldBeNull();
        Check(Region, [Declaring(Town, 9)]).ShouldBeNull();
    }

    /// <summary>
    /// A history row this build cannot parse is skipped, rather than failing the boot it was asked about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found in review, and it would have been the worst regression in the change.</b> This reads descriptor
    /// JSON it did not write — a row recorded by an older build whose model shape differed, or one somebody
    /// edited. An escaping <c>JsonException</c> is neither of the two conflict shapes
    /// <c>AlvoBootService.IsAnotherWriterGettingThereFirst</c> retries, so it propagated out of
    /// <c>StartingAsync</c>: one unreadable row anywhere in a project's history would have crash-looped
    /// <em>every</em> later schema-changing boot, permanently, recoverable only by editing the database. That is
    /// a strictly worse outage than the one this whole type exists to remove.
    /// </para>
    /// <para>
    /// Skipping is the honest answer as well as the safe one: the booting descriptor parsed at stage 0, so a row
    /// that does not parse cannot be it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_history_row_that_cannot_be_read_is_skipped_rather_than_failing_the_boot()
    {
        Check(Region, ["{ this is not json", Town]).ShouldBeNull(
            "an unreadable row is not this descriptor, so a forward deploy still applies");

        Check(City, [City, "{ this is not json", Town]).ShouldNotBeNull(
            "and the rows that can be read must still be compared")
            .Headline.ShouldContain("revision 1");
    }

    /// <summary>
    /// An unreadable <em>current</em> row costs the declared-revision override, not the boot.
    /// </summary>
    /// <remarks>
    /// The override has to parse the current row to read the counter it declares. When that fails the answer is
    /// "no override", which is the same answer as "it declares none" — and the history comparison still runs, so
    /// an older pod is still caught by the rows that can be read.
    /// </remarks>
    [Fact]
    public void An_unreadable_current_row_costs_the_override_not_the_boot()
        => Check(Declaring(City, 1), [Declaring(City, 1), "{ this is not json"]).ShouldNotBeNull(
                "the history comparison must still run over the rows that can be read")
            .Headline.ShouldContain("already applied");

    private static OutOfOrderBoot? Check(string bootingJson, IReadOnlyList<string> historyJson) =>
        DescriptorHistoryOrder.Check(
            NullLogger.Instance,
            AlvoDescriptor.Parse(bootingJson),
            bootingJson,
            [.. historyJson.Select(AppliedAs)]);

    private static DescriptorVersion AppliedAs(string descriptorJson, int index) =>
        new(new SchemaModel([]), descriptorJson, index + 1, DateTimeOffset.UtcNow);

    private static string Declaring(string descriptorJson, int revision) =>
        AlvoDescriptor.Serialize(AlvoDescriptor.Parse(descriptorJson) with { Revision = revision });
}
