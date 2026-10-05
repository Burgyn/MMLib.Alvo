using MMLib.Alvo.Admin.Components.Integrations;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Management.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>Every place the hooks editor restates a core rule gives the core's answer.</b>
/// </summary>
/// <remarks>
/// The dashboard cannot reference <c>MMLib.Alvo</c>, so the URL rule, the mutate literal fit, the template roots and the
/// statement's claims are restated there (spec §6, §11). This suite sees both assemblies' internals, so the agreement is
/// measured here — the <c>DeclaredSlotsAgreementTests</c> pattern — against the real validator, not a copy of it.
/// </remarks>
public sealed class HooksEditorAgreementTests
{
    /// <summary>The endpoint sheet refuses a URL exactly when apply refuses it.</summary>
    /// <remarks>
    /// The agreement holds for an endpoint a hook references: apply checks an endpoint's URL only then, which is why the
    /// probe descriptor gives the endpoint an <c>afterCreate</c> webhook hook. The sheet trims the box before it checks
    /// (<c>EndpointDraft.Refusals</c>), so a URL with surrounding blanks is the trimmed URL's case, not a row here.
    /// </remarks>
    /// <param name="url">The URL typed.</param>
    [Theory]
    [InlineData("https://example.com/hooks")]
    [InlineData("http://localhost:5081/hooks")]
    [InlineData("http://127.0.0.1:5081/hooks")]
    [InlineData("http://[::1]:5081/hooks")]
    [InlineData("http://127.0.0.2/hooks")]
    [InlineData("http://example.com/hooks")]
    [InlineData("ftp://example.com/x")]
    [InlineData("/hooks/relative")]
    [InlineData("htp://x")]
    [InlineData("https://")]
    [InlineData("https://user:pass@example.com/h")]
    [InlineData("")]
    public void The_endpoint_sheet_refuses_a_url_exactly_when_apply_does(string url)
    {
        var applyRefuses = Errors(WithWebhookTo(url)).Any(error => error.Path.EndsWith("/webhooks/endpoints/desk/url", StringComparison.Ordinal));

        (EndpointDraft.UrlRefusal(url) is not null).ShouldBe(applyRefuses, url);
    }

    /// <summary>A literal the row accepts for a field of each type is one apply accepts for that field.</summary>
    /// <remarks>
    /// The enum row holds a declared value, so it passes before and after #308 makes apply refuse a non-member; the fact
    /// that both refuse one is added once #308 is on this branch (preflight C7).
    /// </remarks>
    [Theory]
    [InlineData("integer", "3")]
    [InlineData("integer", "-2")]
    [InlineData("integer", "9223372036854775807")]
    [InlineData("decimal", "12.5")]
    [InlineData("decimal", "-0.01")]
    [InlineData("boolean", "true")]
    [InlineData("datetime", "2026-10-05T12:00:00Z")]
    [InlineData("uuid", "3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f")]
    [InlineData("string", "it's")]
    [InlineData("text", "")]
    [InlineData("enum", "open")]
    public void A_literal_the_mutate_row_accepts_is_one_apply_accepts(string type, string text)
    {
        var descriptor = Probe(type);
        var field = HookFields.Declared(descriptor, "probe")["f"];

        MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, text), field, out var value, out var refusal).ShouldBeTrue(refusal);

        MutateErrors(descriptor, value).ShouldBeEmpty($"{type} '{text}'");
    }

    [Theory]
    [InlineData("date", "2026-10-05")]
    [InlineData("datetime", "tomorrow")]
    [InlineData("datetime", "2026-10-05 12:00")]
    [InlineData("uuid", "42")]
    [InlineData("uuid", "3F2C1A9E-6B7D-4C8E-9F10-2A3B4C5D6E7F")]
    public void The_mutate_row_and_apply_agree_on_text_typed_for_a_moment_or_an_id(string type, string text)
    {
        var descriptor = Probe(type);
        var field = HookFields.Declared(descriptor, "probe")["f"];
        var rowAccepts = MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, text), field, out _, out _);

        (MutateErrors(descriptor, JsonValue.Create(text)).Count == 0).ShouldBe(rowAccepts, $"{type} '{text}'");
    }

    /// <summary>
    /// A date with no time, typed for a <c>datetime</c>: today's answer from both, stated rather than only compared, so a
    /// change on either side names itself (preflight W1).
    /// </summary>
    /// <remarks>
    /// <c>TryGetDateTimeOffset</c> reads <c>2026-10-05</c> as midnight, so the row and apply both accept it today. #308
    /// tightens apply's literal checks: slice D re-runs this fact first once #308 is on this branch, and if apply then
    /// refuses the text, <c>MutateLiteral</c> follows apply and this fact's expected answer changes with it.
    /// </remarks>
    [Fact]
    public void A_date_with_no_time_typed_for_a_datetime_is_accepted_by_the_row_and_by_apply_today()
    {
        var descriptor = Probe("datetime");
        var field = HookFields.Declared(descriptor, "probe")["f"];

        MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, "2026-10-05"), field, out _, out var refusal)
            .ShouldBeTrue(refusal);
        MutateErrors(descriptor, JsonValue.Create("2026-10-05")).ShouldBeEmpty(
            "if apply now refuses a date-only datetime literal, MutateLiteral follows apply (preflight W1)");
    }

    [Fact]
    public void The_template_sheet_knows_exactly_the_roots_an_email_resolves()
        => TemplateDraft.Roots.ShouldBe(TemplatePlaceholder.Roots);

    [Fact]
    public void The_builds_webhooks_sentence_still_says_deliveries_are_unsigned_and_unprojected()
    {
        var sentence = CapabilityReport.Project().Warned.Single(block => block.Block == "webhooks").Consequence;

        sentence.ShouldContain("no delivery is signed", Case.Sensitive, "IntegrationStatement.Title says so; when signing lands (#120) both change");
        sentence.ShouldContain("#152", Case.Sensitive, "IntegrationStatement.HiddenFields says so; projection retires it");
        IntegrationStatement.Title.ShouldContain("not signed", Case.Sensitive, "the core still says no delivery is signed");
        IntegrationStatement.HiddenFields.ShouldContain("'hidden' included", Case.Sensitive, "the core still projects nothing (#152)");
    }

    [Fact]
    public void The_setting_the_statement_names_is_the_options_own_and_admits_nothing_by_default()
    {
        IntegrationStatement.AllowedNetworksSetting.ShouldBe($"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.WebhookAllowedNetworks)}");
        new AlvoEventOptions().WebhookAllowedNetworks.ShouldBeEmpty();
    }

    private static string WithWebhookTo(string url) => new JsonObject
    {
        ["apiVersion"] = "alvo.dev/v1",
        ["name"] = "agreement",
        ["webhooks"] = new JsonObject
        {
            ["endpoints"] = new JsonObject { ["desk"] = new JsonObject { ["url"] = url, ["secretRef"] = "desk-key" } },
        },
        ["entities"] = new JsonObject
        {
            ["orders"] = new JsonObject
            {
                ["fields"] = new JsonObject { ["total"] = new JsonObject { ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2 } },
                ["hooks"] = new JsonObject
                {
                    ["afterCreate"] = new JsonArray(new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "webhook", ["endpoint"] = "desk" },
                    }),
                },
            },
        },
    }.ToJsonString();

    private static string Probe(string type)
    {
        var field = new JsonObject { ["type"] = type };
        if (type == "decimal")
        {
            field["precision"] = 18;
            field["scale"] = 4;
        }
        else if (type == "enum")
        {
            field["values"] = new JsonArray("open", "closed");
        }

        return new JsonObject
        {
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "agreement",
            ["entities"] = new JsonObject { ["probe"] = new JsonObject { ["fields"] = new JsonObject { ["f"] = field } } },
        }.ToJsonString();
    }

    private static List<DescriptorValidationError> MutateErrors(string descriptor, JsonNode? value)
    {
        var root = JsonNode.Parse(descriptor)!;
        root["entities"]!["probe"]!["hooks"] = new JsonObject
        {
            ["beforeUpdate"] = new JsonArray(new JsonObject
            {
                ["action"] = new JsonObject { ["mutate"] = new JsonObject { ["f"] = value?.DeepClone() } },
            }),
        };

        return [.. Errors(root.ToJsonString()).Where(error => error.Path.Contains("/hooks/beforeUpdate/0", StringComparison.Ordinal))];
    }

    private static List<DescriptorValidationError> Errors(string json)
        => [.. new DescriptorValidator().Validate(json).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
}
