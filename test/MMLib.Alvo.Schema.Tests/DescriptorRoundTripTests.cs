using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Schema.Tests;

/// <summary>
/// Proves the public typed descriptor model (<see cref="AlvoDescriptor"/>) round-trips
/// the real examples losslessly: parse → serialize preserves every member (including
/// <c>x-</c> extension keys, tagged CEL, automation, webhooks, functions), the result
/// still validates against the schema, and re-parsing is a stable fixed point.
/// Equality is asserted structurally via the shared <see cref="Canonicalizer"/> (member
/// order and whitespace are not significant); exact-byte identity is not required.
/// </summary>
public class DescriptorRoundTripTests
{
    public static IEnumerable<object[]> Examples() =>
        SchemaPaths.Examples().Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(Examples))]
    public void Serialize_of_parse_is_lossless_against_the_original(string path)
    {
        string original = File.ReadAllText(path);

        string serialized = AlvoDescriptor.Serialize(AlvoDescriptor.Parse(original));

        Canonicalizer.Canonicalize(serialized)
            .ShouldBe(Canonicalizer.Canonicalize(original),
                $"parse → serialize must preserve every member of {Path.GetFileName(path)}");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Serialized_output_still_validates_against_the_schema(string path)
    {
        var schema = SchemaValidator.Load();

        string serialized = AlvoDescriptor.Serialize(AlvoDescriptor.Parse(File.ReadAllText(path)));

        SchemaValidator.Failures(schema, serialized)
            .ShouldBeEmpty($"serialized {Path.GetFileName(path)} must still validate against the schema");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Reparse_is_a_stable_fixed_point(string path)
    {
        string once = AlvoDescriptor.Serialize(AlvoDescriptor.Parse(File.ReadAllText(path)));
        string twice = AlvoDescriptor.Serialize(AlvoDescriptor.Parse(once));

        Canonicalizer.Canonicalize(twice)
            .ShouldBe(Canonicalizer.Canonicalize(once),
                $"model → json → model → json must be stable for {Path.GetFileName(path)}");
    }

    [Fact]
    public void Complex_crm_exercises_the_full_surface_and_preserves_it()
    {
        string path = SchemaPaths.Examples().First(p => p.Contains("complex-crm"));
        var descriptor = AlvoDescriptor.Parse(File.ReadAllText(path));

        AssertExtensionKeysSurvive(descriptor);
        AssertTaggedCelAndComputedSurvive(descriptor);
        AssertAutomationSurvives(descriptor);
        AssertWebhooksAndFunctionsSurvive(descriptor);
        AssertRenameAndRollupViaSurvive(descriptor);
    }

    private static void AssertExtensionKeysSurvive(AlvoDescriptor descriptor)
    {
        descriptor.Extensions.ShouldNotBeNull().ShouldContainKey("x-generator");

        var vatTotal = descriptor.Entities["invoices"].Fields["vat_total"];
        vatTotal.Extensions.ShouldNotBeNull().ShouldContainKey("x-note");
    }

    private static void AssertTaggedCelAndComputedSurvive(AlvoDescriptor descriptor)
    {
        var ownerDefault = descriptor.Entities["companies"].Fields["owner_id"].Default.ShouldNotBeNull();
        ownerDefault.IsExpression.ShouldBeTrue();
        ownerDefault.Expression.ShouldBe("@user.id");

        descriptor.Entities["invoices"].Fields["gross_total"].Computed.ShouldBe("net_total + vat_total");

        var commissionHidden = descriptor.Entities["deals"].Fields["commission_note"].Hidden.ShouldNotBeNull();
        commissionHidden.IsExpression.ShouldBeTrue();
        commissionHidden.Expression.ShouldBe("@user.role != 'finance'");
    }

    private static void AssertAutomationSurvives(AlvoDescriptor descriptor)
    {
        var automation = descriptor.Automation.ShouldNotBeNull();

        var dealWon = automation["deal-won"];
        dealWon.Trigger.Event.ShouldBe("entity.deals.updated");
        dealWon.Actions[0].ShouldBeOfType<WebhookAction>().Endpoint.ShouldBe("invoicing");
        dealWon.Actions[1].ShouldBeOfType<EmailAction>().Template.ShouldBe("deal-won");

        var bulk = automation["bulk-import-index"];
        bulk.Delivery.ShouldBe(DeliveryMode.Batch);
        bulk.Actions[0].ShouldBeOfType<HttpCallAction>().Method.ShouldBe(HttpVerb.Post);

        var stale = automation["stale-deal-reminder"];
        stale.Trigger.Schedule.ShouldBe("0 8 * * MON");
        stale.Actions[0].ShouldBeOfType<FunctionAction>().Name.ShouldBe("remind-stale-deals");
    }

    private static void AssertWebhooksAndFunctionsSurvive(AlvoDescriptor descriptor)
    {
        descriptor.Webhooks.ShouldNotBeNull().Endpoints.ShouldNotBeNull()["invoicing"].SecretRef
            .ShouldBe("invoicing-webhook-secret");

        descriptor.Functions.ShouldNotBeNull()["remind-stale-deals"].Execution.ShouldBe(FunctionExecution.Queued);
    }

    private static void AssertRenameAndRollupViaSurvive(AlvoDescriptor descriptor)
    {
        descriptor.Entities["invoice_items"].RenamedFrom.ShouldBe("line_items");

        var netTotalRollup = descriptor.Entities["invoices"].Fields["net_total"].Rollup.ShouldNotBeNull();
        netTotalRollup.From.ShouldBe("invoice_items");
        netTotalRollup.Op.ShouldBe(RollupOp.Sum);
        netTotalRollup.Via.ShouldBe("invoice_id");
    }
}
