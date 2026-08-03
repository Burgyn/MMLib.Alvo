using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing;
using MMLib.Alvo.Tests.Expressions;

using System.Text.Json.Nodes;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// The three things this build refuses inside an after-hook action, each by name: a raw JSONata expression, an
/// action type the schema declares and nothing runs, and a template whose body lives in a bundle file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a raw JSONata expression is an error rather than a warning.</b>
/// <see cref="UnhonouredSubsystems"/> warns about what an author observes the absence of;
/// <see cref="UnhonouredFeatures"/> refuses what silently produces wrong data. An unevaluated transform is
/// the second kind: the action still runs, but with Alvo's canonical envelope instead of the body that was
/// declared — a delivery that succeeded carrying data the author never wrote, which is indistinguishable
/// from a bug in the consumer.
/// </para>
/// <para>
/// <b>The words themselves are pinned elsewhere, on purpose.</b> Asserting a refusal message with
/// <c>ShouldContain</c> is the most vacuity-prone assertion in this repository — a message that names every
/// known field satisfies almost any substring. So the facts here assert the <em>pointer</em> and identity
/// with the one authority that owns the words, and the words are frozen as a reviewed Verify baseline by
/// <c>UnhonouredFeaturesTests.Every_unhonoured_slot_is_pinned</c>.
/// </para>
/// </remarks>
public class UnhonouredJsonataTests
{
    /// <summary>The three action types the frozen schema declares and this build never runs.</summary>
    private static readonly string[] _unrunnableActionTypes = ["function", "http.call", "entity.update"];

    [Fact]
    public void A_raw_jsonata_webhook_payload_is_refused_at_apply_with_a_pointer_and_a_fix()
    {
        var refusal = Should.Throw<DescriptorValidationException>(
            () => Apply(Webhook(payload: "$merge([new, {\"source\": \"alvo\"}])")));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/action/payload");
        error.Message.ShouldBe(UnhonouredFeatures.RawJsonata.Consequence);
        error.FixSuggestion.ShouldBe(UnhonouredFeatures.RawJsonata.Fix);
    }

    /// <summary>
    /// The other side of the classifier, and the fact that keeps the one above about <em>classification</em>
    /// rather than about JSONata being mentioned: the same slot carrying a template applies.
    /// </summary>
    [Fact]
    public void A_template_webhook_payload_applies()
        => Should.NotThrow(() => Apply(Webhook(payload: "{{new.title}}")));

    /// <summary>
    /// <c>email.data</c> is refused as a <b>dead slot</b>, whatever it carries — the classifier never sees it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The slot was compiled, its placeholders resolved against the entity's schema, and then read by nothing:
    /// the executor renders only <c>to</c>, <c>subject</c> and <c>body</c>, and no <c>data.*</c> placeholder root
    /// exists for either to reach it with. So an author following the schema's own doc comment got a clean apply
    /// and a silently discarded value — the identical failure mode raw JSONata is refused for, at an
    /// implementation rate of zero.
    /// </para>
    /// <para>
    /// Both spellings are asserted, and that is the point of the theory rather than decoration: a template
    /// <em>and</em> a brace-free JSONata expression get the same refusal, because the reason is the slot and not
    /// its contents. A fact over the JSONata spelling alone would keep passing if the template spelling started
    /// compiling into a value nothing renders again.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("records.id")]
    [InlineData("{{new.title}}")]
    public void An_email_data_slot_is_refused_at_apply_whatever_it_carries(string data)
    {
        var refusal = Should.Throw<DescriptorValidationException>(() => Apply(Email(data: data)));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/action/data");
        error.Message.ShouldBe(UnhonouredFeatures.EmailData.Consequence);
        error.FixSuggestion.ShouldBe(UnhonouredFeatures.EmailData.Fix);
    }

    /// <summary>
    /// Deviation 65: with no evaluator, "JSONata never runs in-transaction" is <b>vacuous</b>, so this is an
    /// <em>absence</em> test named as one. A test called "JSONata does not run in-transaction" would be green
    /// forever and would read as though the ban were enforced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real ban test is owed by the PR that introduces an evaluator, and it must be
    /// <b>architectural</b> — nothing on the in-transaction path can reach the evaluator — not behavioural,
    /// because a behavioural test only samples the paths someone thought of. Tracked in issue #149.
    /// </para>
    /// <para>
    /// Each allowed file is allowed for a stated reason, and none of them can evaluate anything:
    /// <c>JsonataSlot.cs</c> is the classifier that refuses it, <c>UnhonouredFeatures.cs</c> words the
    /// refusal, and <c>AfterHookCompiler.cs</c> is the one caller of the classifier. Comments are stripped
    /// before the search, so the XML docs that explain the absence are not mistaken for it.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_jsonata_evaluator_exists_on_any_path()
        => ShippedSources.FileNamesMentioning("jsonata").ShouldBe(
            ["JsonataSlot.cs", "UnhonouredFeatures.cs", "AfterHookCompiler.cs"],
            ignoreOrder: true,
            "the only code mentioning JSONata is the classifier that refuses it, the table that words the "
            + "refusal, and the compiler that asks the classifier; anything else is an evaluator "
            + "(deviation 65, issue #149)");

    /// <summary>
    /// Deviation 66: <c>function</c> and <c>http.call</c> are frozen into <c>$defs/action</c> and neither is
    /// implemented; <c>entity.update</c> is PR5b's. All three are refused <b>by name</b>, each naming what
    /// does not happen.
    /// </summary>
    /// <param name="type">The action's <c>type</c> discriminator.</param>
    [Theory]
    [InlineData("function")]
    [InlineData("http.call")]
    [InlineData("entity.update")]
    public void An_after_hook_action_this_build_does_not_run_is_refused_by_name(string type)
    {
        var refusal = Should.Throw<DescriptorValidationException>(() => Apply(ActionOfType(type)));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/action/type");
        error.Message.ShouldBe(UnhonouredFeatures.UnhonouredAction(type).Consequence);
        error.Message.ShouldContain(type);
        error.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Each of the three names what specifically does not happen, so the three messages are not one message
    /// with the type interpolated into it.
    /// </summary>
    /// <param name="type">The action's <c>type</c> discriminator.</param>
    /// <param name="named">A consequence of refusing exactly that action, which no other of the three shares.</param>
    [Theory]
    [InlineData("function", "functions")]
    [InlineData("http.call", "headersSecretRef")]
    [InlineData("entity.update", "no record is written")]
    public void Each_refused_action_names_its_own_consequence(string type, string named)
    {
        var consequences = _unrunnableActionTypes
            .ToDictionary(candidate => candidate, candidate => UnhonouredFeatures.UnhonouredAction(candidate).Consequence, StringComparer.Ordinal);

        consequences[type].ShouldContain(named);
        consequences.Where(entry => entry.Key != type).ShouldAllBe(entry => !entry.Value.Contains(named, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Every action type the frozen schema declares is either honoured or refused by name.</b> The expected
    /// set is read from <c>schema/project.schema.json</c>, so a sixth action type added to the schema fails
    /// here rather than being silently accepted and running nothing.
    /// </summary>
    [Fact]
    public void Every_action_type_the_frozen_schema_declares_is_named()
    {
        var declared = SchemaActionTypes();

        declared.ShouldBe(
            ["webhook", "email", "function", "entity.update", "http.call"],
            ignoreOrder: true,
            "read from the frozen schema — if this changed, the schema changed and the action switch owes it "
            + "a visit");
        EveryActionShape().Select(ActionType.NameOf).ShouldBe(declared, ignoreOrder: true);
    }

    /// <summary>
    /// A template whose body lives in a bundle file is refused when an after-hook references it: nothing in
    /// this build reads a file out of a descriptor bundle, so the alternative is an email with an empty body —
    /// the silent-wrong-output failure the whole table exists for.
    /// </summary>
    [Fact]
    public void A_template_body_file_is_refused_on_the_templates_own_pointer()
    {
        var refusal = Should.Throw<DescriptorValidationException>(() => Apply(Email(), bodyFile: "deal-won.md"));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/templates/deal-won/bodyFile");
        error.Message.ShouldBe(UnhonouredFeatures.TemplateBodyFile.Consequence);
    }

    /// <summary>
    /// A template nothing references keeps its <see cref="UnhonouredSubsystems"/> warning and is not refused —
    /// the refusal belongs to the hook that would have rendered it, not to the block.
    /// </summary>
    [Fact]
    public void A_body_file_on_a_template_no_hook_references_is_not_refused()
        => Should.NotThrow(() => Apply(Webhook(), bodyFile: "deal-won.md"));

    /// <summary>
    /// The whole apply path, in the order a host runs it: map the descriptor to a schema, then compile every
    /// rule and hook against that schema. The mapper is deliberately included — it is the pass that refused
    /// an <c>after*</c> hook point before PR5a, so a fixture reaching the compiler at all is half the fact.
    /// </summary>
    /// <param name="hooks">The entity's hooks.</param>
    /// <param name="bodyFile">A <c>bodyFile</c> to put on the referenced template, when the fixture needs one.</param>
    private static PolicyCatalog Apply(EntityHooks hooks, string? bodyFile = null)
    {
        var descriptor = Descriptor(hooks, bodyFile);

        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler);
    }

    private static EntityHooks Webhook(string? payload = null) =>
        After(new WebhookAction { Endpoint = "crm-sync", Payload = payload });

    private static EntityHooks Email(string? data = null) =>
        After(new EmailAction { Template = "deal-won", To = "ops@firma.sk", Data = data });

    private static EntityHooks ActionOfType(string type) => After(type switch
    {
        "function" => new FunctionAction { Name = "recalculate" },
        "http.call" => new HttpCallAction { Url = "https://example.test/call" },
        "entity.update" => new EntityUpdateAction
        {
            Entity = "deals",
            Payload = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal) { ["title"] = ValueOrExpr.FromExpression("new.title") },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an action type this fixture knows."),
    });

    private static IEnumerable<AutomationAction> EveryActionShape() =>
    [
        new WebhookAction { Endpoint = "crm-sync" },
        new EmailAction { Template = "deal-won", To = "ops@firma.sk" },
        new FunctionAction { Name = "recalculate" },
        new EntityUpdateAction
        {
            Entity = "deals",
            Payload = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal) { ["title"] = ValueOrExpr.FromExpression("new.title") },
        },
        new HttpCallAction { Url = "https://example.test/call" },
    ];

    private static EntityHooks After(AutomationAction action) =>
        new() { AfterUpdate = [new AfterHook { Action = action }] };

    private static AlvoDescriptor Descriptor(EntityHooks hooks, string? bodyFile) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "test",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["deals"] = new()
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.String, MaxLength = 200 },
                },
                Hooks = hooks,
            },
        },
        Templates = new Dictionary<string, MessageTemplate>(StringComparer.Ordinal)
        {
            ["deal-won"] = new() { Subject = "Deal won", Body = bodyFile is null ? "Closed." : null, BodyFile = bodyFile },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                ["crm-sync"] = new() { Url = "https://example.test/hook", SecretRef = "crm-sync-secret" },
            },
        },
    };

    /// <summary>The <c>type</c> discriminator of every branch of the frozen schema's <c>$defs/action</c>.</summary>
    private static IReadOnlyList<string> SchemaActionTypes()
    {
        var schema = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;

        return [.. schema["$defs"]!["action"]!["oneOf"]!.AsArray()
            .Select(branch => branch!["properties"]!["type"]!["const"]!.GetValue<string>())];
    }
}
