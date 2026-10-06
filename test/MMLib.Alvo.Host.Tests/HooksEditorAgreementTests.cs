using MMLib.Alvo.Admin.Components.Integrations;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
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
    /// The enum row holds a declared value, so it passes before and after #308 makes apply refuse a non-member; that both
    /// refuse a non-member is <see cref="A_literal_the_row_refuses_for_a_facet_apply_refuses_too"/> (preflight C7).
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
    /// tightened apply's literal checks; slice D re-ran this fact first once #308 was on its branch, and apply still
    /// accepts the text. If apply ever refuses it, <c>MutateLiteral</c> follows apply and this fact's answer changes with it.
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

    /// <summary>
    /// Pre-flight C7, owed once C1's facet check (#308) reached this branch: an enum literal outside its values is refused
    /// by the mutate row and by apply alike.
    /// </summary>
    /// <param name="type">The probe field's type.</param>
    /// <param name="literal">The literal typed into the row.</param>
    [Theory]
    [InlineData("enum", "bogus")]
    [InlineData("enum", "Open")]
    public void A_literal_the_row_refuses_for_a_facet_apply_refuses_too(string type, string literal)
    {
        var descriptor = Probe(type);
        var field = HookFields.Declared(descriptor, "probe")["f"];

        MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, literal), field, out _, out var refusal).ShouldBeFalse();
        refusal.ShouldNotBeNullOrWhiteSpace();
        MutateErrors(descriptor, JsonValue.Create(literal)).ShouldNotBeEmpty("apply refuses a value outside the enum's declared values since #308");
    }

    /// <summary>
    /// Pre-flight C7: a hook that sets a required field to empty is refused by the row and by apply alike — apply writes
    /// a JSON <c>null</c> into the mutate map and refuses it there, before any write.
    /// </summary>
    [Fact]
    public void Setting_a_required_field_to_empty_is_refused_by_the_row_and_by_apply()
    {
        var descriptor = Probe("string", required: true);
        var field = HookFields.Declared(descriptor, "probe")["f"];

        MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, string.Empty) { Empty = true }, field, out _, out var refusal)
            .ShouldBeFalse();
        refusal.ShouldNotBeNullOrWhiteSpace();
        MutateErrors(descriptor, value: null).ShouldNotBeEmpty("a null for a required field is refused at apply (C1 Ruling V)");
    }

    /// <summary>
    /// The guided rows refuse a negative number by their own choice (B3: the client may be stricter only when it says so):
    /// apply accepts the same comparison written in text, which is where the rows' refusal sends the writer.
    /// </summary>
    [Fact]
    public void A_negative_number_the_rows_refuse_is_one_apply_accepts_in_text()
    {
        var row = new ConditionRow(ConditionOperator.Less, RowImage.New, "f", ConditionFieldKind.Number, "-5");

        ConditionText.Refusal(new GuidedCondition(true, [row])).ShouldNotBeNull().ShouldContain("text mode");
        ConditionErrors(Probe("integer"), "new.f < -5").ShouldBeEmpty("slice D admits arithmetic, and so a negated literal, in a condition");
    }

    /// <summary>A value the guided condition quotes is read back by the real lexer as exactly that value, in one literal.</summary>
    /// <param name="value">The value typed.</param>
    [Theory]
    [InlineData("ready")]
    [InlineData("it's")]
    [InlineData("back\\slash")]
    [InlineData("say \"hi\"")]
    [InlineData("line\nbreak\r\ttab")]
    [InlineData("čaj")]
    [InlineData("中文")]
    [InlineData("\U0001F600")]
    [InlineData("' || true || '")]
    [InlineData("{v} && {r}")]
    [InlineData("\\'")]
    [InlineData("")]
    public void A_quoted_condition_value_lexes_back_to_the_value(string value)
    {
        var tokens = CelLexer.Tokenize(ConditionText.Quote(value));

        tokens.Count.ShouldBe(2, value);
        tokens[0].Kind.ShouldBe(CelTokenKind.StringLiteral);
        tokens[0].Text.ShouldBe(value);
    }

    /// <summary>
    /// A value typed into a number box that is not a number never becomes CEL syntax: the row is safe on its own, even
    /// while the form shows the refusal (review I1).
    /// </summary>
    /// <param name="value">The value typed.</param>
    [Theory]
    [InlineData("0 || true")]
    [InlineData("0) || (true")]
    [InlineData("1 && false")]
    [InlineData("-5")]
    [InlineData("1e3")]
    [InlineData("12\n")]
    public void A_number_box_holding_anything_but_a_number_writes_no_operator_of_its_own(string value)
    {
        var cel = ConditionText.Row(new ConditionRow(ConditionOperator.Less, RowImage.New, "quantity", ConditionFieldKind.Number, value));

        CelLexer.Tokenize(cel).Select(token => token.Kind).ShouldBe(
            [CelTokenKind.Identifier, CelTokenKind.Dot, CelTokenKind.Identifier, CelTokenKind.Less, CelTokenKind.StringLiteral, CelTokenKind.EndOfInput],
            cel);
        RightLiteral(CelParser.Parse(cel), CelBinaryOperator.Less).ShouldBe(value);
    }

    /// <summary>A number box holding a number writes it bare, which the real parser reads as that number.</summary>
    /// <param name="value">The value typed.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("12")]
    [InlineData("4.5")]
    public void A_number_box_holding_a_number_writes_it_bare(string value)
    {
        var cel = ConditionText.Row(new ConditionRow(ConditionOperator.Less, RowImage.New, "quantity", ConditionFieldKind.Number, value));

        cel.ShouldBe($"new.quantity < {value}");
        CelParser.Parse(cel).ShouldBeOfType<CelBinary>().Right.ShouldBeOfType<CelLiteral>().Type.ShouldNotBe(CelValueType.String);
    }

    /// <summary>
    /// A hostile value in a text row or a role row is read by the real parser as one comparison whose literal is exactly
    /// the value (review minor 1).
    /// </summary>
    /// <param name="value">The value typed.</param>
    [Theory]
    [InlineData("' || true || '")]
    [InlineData("x') || ('y")]
    [InlineData("\\' || true || \\'")]
    [InlineData("a\" || true || \"b")]
    [InlineData("line\nbreak")]
    [InlineData("čaj 中文")]
    [InlineData("\u0645\u06CC\u200C\u062E \U0001F468\u200D\U0001F469")]
    [InlineData("{v} {r} {f} {n}")]
    public void A_hostile_value_stays_one_literal_of_one_comparison(string value)
    {
        var text = ConditionText.Row(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, value));
        var role = ConditionText.Row(new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, value));

        var equality = CelParser.Parse(text);
        equality.ShouldBeOfType<CelBinary>().Left.ShouldBeOfType<CelFieldRef>().FieldName.ShouldBe("title");
        RightLiteral(equality, CelBinaryOperator.Equal).ShouldBe(value);

        var membership = CelParser.Parse(role).ShouldBeOfType<CelBinary>();
        membership.Operator.ShouldBe(CelBinaryOperator.In);
        membership.Left.ShouldBeOfType<CelLiteral>().Value.ShouldBe(value);
        membership.Right.ShouldBeOfType<CelContextRef>().Value.ShouldBe(CelContextValue.UserRoles);
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

    private static object? RightLiteral(CelNode node, CelBinaryOperator relation)
    {
        var binary = node.ShouldBeOfType<CelBinary>();
        binary.Operator.ShouldBe(relation);
        var literal = binary.Right.ShouldBeOfType<CelLiteral>();
        literal.Type.ShouldBe(CelValueType.String);
        return literal.Value;
    }

    private static string Probe(string type, bool required = false)
    {
        var field = new JsonObject { ["type"] = type };
        if (required)
        {
            field["required"] = true;
        }

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

    private static List<DescriptorValidationError> ConditionErrors(string descriptor, string condition)
    {
        var root = JsonNode.Parse(descriptor)!;
        root["entities"]!["probe"]!["hooks"] = new JsonObject
        {
            ["beforeUpdate"] = new JsonArray(new JsonObject
            {
                ["condition"] = condition,
                ["action"] = new JsonObject { ["reject"] = "probe" },
            }),
        };

        return [.. Errors(root.ToJsonString()).Where(error => error.Path.Contains("/hooks/beforeUpdate/0", StringComparison.Ordinal))];
    }

    private static List<DescriptorValidationError> Errors(string json)
        => [.. new DescriptorValidator().Validate(json).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
}
