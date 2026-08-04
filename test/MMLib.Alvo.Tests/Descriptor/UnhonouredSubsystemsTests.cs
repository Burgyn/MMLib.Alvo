using Microsoft.Extensions.Logging;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// The startup warning for the descriptor blocks this build parses and honours nowhere — asserted on
/// <b>which blocks the line names</b>, never merely on the fact that a line was written.
/// </summary>
/// <remarks>
/// <para>
/// <b>"A warning was logged" is the assertion this file exists to refuse.</b> It passes on any wording, so
/// it would survive a warning that named the wrong blocks, no blocks, or a set that silently shrank when an
/// entry was deleted from <see cref="UnhonouredSubsystems.All"/> — which is precisely the vacuity
/// <see cref="UnhonouredFeatures"/> already paid for once, where a table-driven theory went on passing after
/// <c>rollup</c> was removed from its own data.
/// </para>
/// <para>
/// <b>So the expected set comes from outside the code under test, twice over.</b>
/// <see cref="Every_unhonoured_subsystem_names_a_block_the_schema_declares"/> reads
/// <c>schema/project.schema.json</c>, which is what makes the set <em>right</em> rather than merely
/// unchanged; and <see cref="The_warning_names_every_unhonoured_block_the_showcase_declares"/> spells the
/// five names as a literal beside a fixture that was authored for a different purpose entirely. Deleting an
/// entry fails both.
/// </para>
/// <para>
/// <b><c>examples/complex-crm</c> is the fixture, and it is deliberately not applied here.</b> Applying it
/// fails on purpose (<c>NOT-RUNNABLE.md</c>) because it also declares four refused <em>features</em> — so
/// the warning is driven from the parsed descriptor directly.
/// </para>
/// <para>
/// <b>Every fact here therefore proves the warning is <em>correct</em>, and none of them proves it is
/// <em>reached</em>.</b> They all call <see cref="UnhonouredSubsystems.Warn"/> themselves, so deleting the
/// call from the boot's stage 0 left this file — and the whole suite — green while the user-visible
/// deliverable did nothing. That half is pinned by
/// <c>Migrations.DescriptorBootPlanTests.A_declared_but_unhonoured_block_warns_on_every_boot_naming_it</c>
/// and, on the apply path that drives stage 0,
/// <c>Migrations.SchemaMigrationRunnerTests.Applying_a_descriptor_that_declares_an_unhonoured_block_warns_naming_it</c>,
/// over a purpose-built appliable descriptor and a capturing logger provider. Read them together; none
/// is sufficient alone, and this file used to be presented as if it were.
/// </para>
/// </remarks>
public class UnhonouredSubsystemsTests
{
    /// <summary>
    /// The five blocks the format showcase declares, named as a literal — the pin that a table-driven
    /// assertion structurally cannot be.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from <see cref="UnhonouredSubsystems.All"/> on purpose: a set read off
    /// the subject shrinks with it. The literal is a second, independent statement of the same fact, and the
    /// two disagreeing is the whole signal.
    /// </remarks>
    private static readonly string[] _blocksComplexCrmDeclares =
        ["dynamicEntities", "automation", "templates", "webhooks", "functions"];

    /// <summary>
    /// <b>The warning names each declared-but-unhonoured block</b>, and the fixture is a descriptor that was
    /// written to exercise the whole schema surface rather than to exercise this warning.
    /// </summary>
    [Fact]
    public void The_warning_names_every_unhonoured_block_the_showcase_declares()
    {
        var descriptor = AlvoDescriptor.Parse(File.ReadAllText(ComplexCrm()));
        var logger = new CapturingLogger();

        UnhonouredSubsystems.Warn(logger, descriptor);

        var warning = logger.Warnings.ShouldHaveSingleItem(
            "one line for the whole set — an author reading five separate warnings has to reassemble the "
            + "list the single line already gives them");
        foreach (var block in _blocksComplexCrmDeclares)
        {
            warning.ShouldContain(
                block,
                Shouldly.Case.Sensitive,
                $"'{block}' is declared and honoured nowhere, and a warning that does not name it leaves an "
                + "author debugging the layer above it");
        }
    }

    /// <summary>
    /// The fixture really does declare exactly those five blocks and no sixth — read from the example's own
    /// JSON, so the literal above cannot drift away from the descriptor it describes.
    /// </summary>
    /// <remarks>
    /// Without this, the fact above would still pass if <c>complex-crm</c> gained a sixth unhonoured block:
    /// the loop asserts every expected name is present, not that no other is. This is the other direction,
    /// and it is what keeps the literal honest as the showcase grows.
    /// </remarks>
    [Fact]
    public void The_showcase_declares_exactly_the_blocks_the_expected_set_names()
    {
        var root = JsonNode.Parse(File.ReadAllText(ComplexCrm()))!.AsObject();

        root.Select(property => property.Key)
            .Where(key => UnhonouredSubsystems.All.Any(
                subsystem => string.Equals(subsystem.Block, key, StringComparison.Ordinal)))
            .ShouldBe(
                _blocksComplexCrmDeclares,
                ignoreOrder: true,
                "read from the example itself — if this changed, the showcase changed and the expected set "
                + "above owes it a visit");
    }

    /// <summary>
    /// A descriptor declaring none of them warns about <b>nothing</b> — the direction that catches a warning
    /// wired to fire unconditionally.
    /// </summary>
    /// <remarks>
    /// The one-fact-per-direction rule, and it is not symmetry for its own sake: a line every descriptor earns
    /// is a line every operator filters out, which costs the warning the only thing it has. <c>simple-tasks</c>
    /// declares entities and auth and nothing else, so it is the honest negative.
    /// </remarks>
    [Fact]
    public void A_descriptor_declaring_no_unhonoured_block_warns_about_nothing()
    {
        var path = Path.Combine(Examples(), "simple-tasks", "tasks.alvo.json");
        var descriptor = AlvoDescriptor.Parse(File.ReadAllText(path));
        var logger = new CapturingLogger();

        UnhonouredSubsystems.Warn(logger, descriptor);

        logger.Warnings.ShouldBeEmpty(
            "simple-tasks declares no unhonoured block, and a warning here would be one every descriptor "
            + "earns");
    }

    /// <summary>
    /// <b>Every entry names a real top-level property of the frozen schema.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected set comes from <c>schema/project.schema.json</c>, which is the only place that can say the
    /// set is <em>right</em> rather than merely unchanged — the same anchor
    /// <c>DescriptorValidatorTests.Every_unhonoured_path_names_a_key_the_schema_declares</c> uses for the
    /// feature tables.
    /// </para>
    /// <para>
    /// <b>This is not a formality; it caught both errors in this table's first draft.</b> That draft carried
    /// <c>realtime</c>, which the schema declares per <em>entity</em> and not at the root, so the entry would
    /// have matched nothing on every descriptor forever; and it spelled <c>automation</c> as
    /// <c>automations</c>, the same defect one letter smaller. Neither is visible to a fact driven off the
    /// table, because both produce a predicate that simply never fires.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_unhonoured_subsystem_names_a_block_the_schema_declares()
    {
        JsonNode schema = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
        var declared = schema["properties"]!.AsObject().Select(property => property.Key).ToList();

        UnhonouredSubsystems.All
            .Select(subsystem => subsystem.Block)
            .ShouldBeSubsetOf(
                declared,
                "an entry naming no top-level block the schema declares warns about nothing on every "
                + "descriptor, which is worse than a missing entry because it reads as coverage");
    }

    /// <summary>
    /// <b>The two blocks an after-hook now reaches say so</b>, rather than going on claiming that nothing
    /// renders a template and no event is ever delivered.
    /// </summary>
    /// <remarks>
    /// The consequence is the whole product of an entry — a line that names the right block and describes the
    /// wrong absence sends the author to the wrong layer just as effectively as no line at all. PR5a made both
    /// of these half true: an after-hook <em>does</em> render a template and <em>does</em> post to an endpoint,
    /// while automation still reaches neither. Both halves have to be in the words, which is what these two
    /// substrings check for.
    /// </remarks>
    /// <param name="block">The block whose consequence must name both halves.</param>
    [Theory]
    [InlineData("templates")]
    [InlineData("webhooks")]
    public void The_two_blocks_an_after_hook_reaches_name_both_halves(string block)
    {
        var consequence = Consequence(block);

        consequence.ShouldContain(
            "after-hook", Shouldly.Case.Sensitive, "the honoured half — this build does run these from a hook");
        consequence.ShouldContain(
            "automation", Shouldly.Case.Sensitive, "the unhonoured half — no automation rule is evaluated yet");
    }

    /// <summary>
    /// <b>The <c>webhooks</c> line names that a delivery is unsigned</b>, because that is a <em>security</em>
    /// absence an author reading the old wording would have assumed away.
    /// </summary>
    /// <remarks>
    /// Standard Webhooks signing is 7.1's, so a delivery that happens today carries no HMAC header and
    /// <c>secretRef</c> is never read — and the endpoint declaration <em>requires</em> a <c>secretRef</c>, so
    /// an author has already supplied one and has every reason to believe it is in use. Naming the two
    /// specific absences rather than "signing is not implemented" is deliberate: <c>secretRef</c> is the key
    /// they wrote, and the HMAC header is the thing the receiver looks for and will not find.
    /// </remarks>
    [Fact]
    public void The_webhook_line_names_the_unsigned_delivery_and_the_unread_secret_ref()
    {
        var consequence = Consequence("webhooks");

        consequence.ShouldContain("secretRef", Shouldly.Case.Sensitive);
        consequence.ShouldContain("HMAC", Shouldly.Case.Sensitive);
    }

    /// <summary>One entry's consequence, looked up by block name.</summary>
    /// <param name="block">The block's key at the descriptor root.</param>
    private static string Consequence(string block) =>
        UnhonouredSubsystems.All
            .Single(subsystem => string.Equals(subsystem.Block, block, StringComparison.Ordinal))
            .Consequence;

    /// <summary>The repository's <c>examples/</c> directory.</summary>
    private static string Examples() => Path.Combine(RepositoryRoot.Find(), "examples");

    /// <summary>The format showcase's descriptor, which exercises the whole schema surface.</summary>
    private static string ComplexCrm() =>
        Path.Combine(Examples(), "complex-crm", "crm.alvo.json");
}
