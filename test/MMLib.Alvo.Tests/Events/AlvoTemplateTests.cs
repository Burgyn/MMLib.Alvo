using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The <c>{{…}}</c> template engine: the only transform PR5a honours, because there is no mature .NET
/// JSONata implementation and a hand-rolled subset would deliver a wrong body on a successful delivery.
/// </summary>
/// <remarks>
/// Two rulings from the addendum's Decision 2 are pinned here: a template is validated at <b>apply</b> time
/// against the schema rather than at delivery time against the payload, and an unresolvable placeholder is
/// <b>refused</b> rather than rendered to the empty string.
/// </remarks>
public class AlvoTemplateTests
{
    [Fact]
    public void A_template_renders_literals_and_placeholders_in_order()
    {
        var @event = SampleEvent(record: Record(("title", "Big deal"), ("amount", 1200m)));

        AlvoTemplate.Parse("Deal won: {{new.title}} ({{new.amount}})").Render(@event)
            .ShouldBe("Deal won: Big deal (1200)");
    }

    /// <summary>
    /// Every root the type promises, with the value it resolves to rather than merely "something non-blank" —
    /// a root wired to the wrong envelope attribute renders a non-blank wrong answer.
    /// </summary>
    [Theory]
    [InlineData("{{event.id}}", "019fc77e-be7b-72e8-b7fd-ffd6f6306e3e")]
    [InlineData("{{event.type}}", "entity.deals.updated")]
    [InlineData("{{event.time}}", "2026-08-03T09:30:00.0000000+00:00")]
    [InlineData("{{event.subject}}", "deals/3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("{{@user.id}}", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("{{new.title}}", "Big deal")]
    [InlineData("{{old.title}}", "Small deal")]
    [InlineData("{{ new.title }}", "Big deal")]
    public void Every_documented_root_resolves(string template, string expected)
        => AlvoTemplate.Parse(template).Render(SampleEvent()).ShouldBe(expected);

    /// <summary>
    /// <c>@tenant</c> is absent on purpose, and that is the deviation this list exists to make visible: the
    /// addendum's table promises it, and the envelope carries no tenant attribute to answer it with.
    /// </summary>
    [Fact]
    public void The_roots_are_exactly_the_ones_the_envelope_can_answer()
        => TemplatePlaceholder.Roots.ShouldBe(["new", "old", "event", "@user"]);

    /// <summary>
    /// Deviation 64: an unresolvable placeholder is refused at apply, never rendered to empty.
    /// </summary>
    /// <remarks>
    /// Rendering <c>{{@user.email}}</c> to <c>""</c> yields <c>To: ""</c> — a mail failure that looks like a
    /// broken SMTP server, which is the same misattribution <c>UnhonouredSubsystems</c> exists to prevent.
    /// <c>AlvoContext</c> carries <c>User</c>, <c>Roles</c> and <c>Tenant</c> and no email address, and the
    /// envelope narrows that further: it carries <c>authid</c> and no roles at all.
    /// </remarks>
    [Fact]
    public void The_shipped_examples_unresolvable_recipient_is_refused_and_the_message_names_what_exists()
    {
        TemplatePlaceholder.TryResolve("@user.email", Deals, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("@user.email");
        refusal.ShouldContain("@user.id");
    }

    /// <summary>
    /// The second name the addendum promises and the envelope cannot answer. It is refused <em>by name</em>
    /// rather than as an unknown root, because <c>@tenant.id</c> is a real Alvo CEL context reference — an
    /// author who knows the rule language will write it, and "unknown root" would misdescribe why it fails.
    /// </summary>
    /// <remarks>
    /// Resolving it from the row's own <c>tenant_id</c> was considered and rejected: <c>@tenant.id</c> asks
    /// which tenant the <em>caller</em> was in, and a write made as <c>AlvoContext.System</c> has no tenant
    /// while the row it wrote has one. Answering a different question with a non-blank value is the exact
    /// defect deviation 64 exists to prevent.
    /// </remarks>
    [Fact]
    public void The_tenant_root_is_refused_by_name_and_the_message_names_the_row_field_instead()
    {
        TemplatePlaceholder.TryResolve("@tenant.id", Deals, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("@tenant.id");
        refusal.ShouldContain("new.tenant_id");
    }

    [Fact]
    public void A_placeholder_naming_an_undeclared_field_is_refused_with_a_did_you_mean()
    {
        TemplatePlaceholder.TryResolve("new.titel", Deals, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("titel");
        refusal.ShouldContain("Did you mean 'title'?");
    }

    [Theory]
    [InlineData("new.title")]
    [InlineData("old.title")]
    [InlineData("new.amount")]
    [InlineData("event.time")]
    [InlineData("@user.id")]
    public void A_placeholder_naming_a_declared_field_or_attribute_resolves(string placeholder)
        => TemplatePlaceholder.TryResolve(placeholder, Deals, out _).ShouldBeTrue();

    /// <summary>
    /// A managed column is not in <c>entity.Fields</c>, and an author writing <c>{{new.created_at}}</c> has
    /// named a column the framework really wrote — so the candidate list is the declared fields
    /// <em>plus</em> <see cref="AlvoManagedColumns"/>, which is the same authority the write guard uses.
    /// </summary>
    [Theory]
    [InlineData("new.id")]
    [InlineData("new.tenant_id")]
    [InlineData("new.created_at")]
    public void A_managed_column_resolves_although_the_descriptor_never_declared_it(string placeholder)
    {
        Deals.Fields.ShouldNotContain(field => field.Name == placeholder.Split('.')[1]);

        TemplatePlaceholder.TryResolve(placeholder, Deals, out _).ShouldBeTrue();
    }

    /// <summary>
    /// The managed columns are read from the entity's <em>traits</em>, so a global entity's
    /// <c>{{new.tenant_id}}</c> is refused rather than resolving against a column that does not exist.
    /// </summary>
    [Fact]
    public void A_managed_column_the_entitys_traits_do_not_carry_is_refused()
        => TemplatePlaceholder.TryResolve("new.tenant_id", GlobalDeals, out _).ShouldBeFalse();

    [Fact]
    public void An_unknown_root_is_refused_naming_every_root_that_exists()
    {
        TemplatePlaceholder.TryResolve("record.title", Deals, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull();
        foreach (var root in TemplatePlaceholder.Roots)
        {
            refusal.ShouldContain(root);
        }
    }

    [Fact]
    public void An_unknown_event_attribute_is_refused_naming_the_four_that_exist()
    {
        TemplatePlaceholder.TryResolve("event.source", Deals, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("event.id");
        refusal.ShouldContain("event.type");
        refusal.ShouldContain("event.time");
        refusal.ShouldContain("event.subject");
    }

    [Fact]
    public void A_placeholder_that_is_not_a_root_and_a_member_is_refused()
    {
        TemplatePlaceholder.TryResolve("title", Deals, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("new.title");
    }

    /// <summary>
    /// A value absent from the row renders as the empty string only because the FIELD is declared and its
    /// value is genuinely null — which is a data fact, not an authoring mistake. The authoring mistake is
    /// refused at apply instead, which is the whole point of validating there.
    /// </summary>
    [Fact]
    public void A_declared_field_whose_value_is_null_renders_as_empty()
        => AlvoTemplate.Parse("[{{new.title}}]").Render(SampleEvent(record: Record(("title", null))))
            .ShouldBe("[]");

    /// <summary>
    /// The same ruling for the one nullable attribute: an anonymous caller has no credential, so
    /// <c>authid</c> is null and <c>{{@user.id}}</c> is empty. It is a data fact — nobody acted — and
    /// refusing it at delivery would make every anonymous write's delivery fail permanently, which is
    /// strictly worse than a value that reads as absent because it is.
    /// </summary>
    [Fact]
    public void An_anonymous_events_user_id_renders_as_empty_because_no_credential_acted()
        => AlvoTemplate.Parse("[{{@user.id}}]").Render(SampleEvent() with { AuthType = AlvoEventAuthType.Anonymous, AuthId = null })
            .ShouldBe("[]");

    /// <summary>
    /// A rendered timestamp is the framework's own round-trip form, so a template can never introduce a
    /// second spelling of an instant.
    /// </summary>
    [Fact]
    public void A_rendered_timestamp_is_the_frameworks_round_trip_utc_form()
    {
        var time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

        AlvoTemplate.Parse("{{event.time}}").Render(SampleEvent() with { Time = time })
            .ShouldBe("2026-08-03T09:30:00.0000000+00:00");
    }

    /// <summary>
    /// A rendered boolean is the JSON spelling, for the reason the timestamp is the round-trip form: a
    /// webhook body carrying <c>True</c> is a second spelling of a value the framework's own writer spells
    /// <c>true</c>.
    /// </summary>
    [Fact]
    public void A_rendered_boolean_is_the_json_spelling_not_dot_nets()
        => AlvoTemplate.Parse("{{new.is_closed}}").Render(SampleEvent(record: Record(("is_closed", true))))
            .ShouldBe("true");

    /// <summary>
    /// A rendered value is never re-scanned for placeholders, or a record whose own text contained
    /// <c>{{…}}</c> would inject one.
    /// </summary>
    [Fact]
    public void A_rendered_value_is_never_itself_treated_as_a_template()
        => AlvoTemplate.Parse("{{new.title}}").Render(SampleEvent(record: Record(("title", "{{@user.id}}"))))
            .ShouldBe("{{@user.id}}");

    /// <summary>
    /// An unterminated placeholder is an authoring mistake with a real cost — shipped as literal text it
    /// delivers <c>{{new.title}</c> to the endpoint — so the engine refuses it rather than treating it as
    /// text. A single brace stays text, because a template body legitimately contains one.
    /// </summary>
    [Theory]
    [InlineData("{{new.title}")]
    [InlineData("{{new.{{title}}}}")]
    [InlineData("{{}}")]
    [InlineData("{{   }}")]
    public void An_unterminated_or_empty_placeholder_is_refused_rather_than_shipped_as_literal_text(string source)
        => Should.Throw<ArgumentException>(() => AlvoTemplate.Parse(source));

    /// <summary>
    /// The asymmetry with <see cref="JsonataSlot"/>: in the plain-string sugar slots (<c>email.to</c>,
    /// <c>entity.update.recordId</c>, <c>templates.subject</c>/<c>body</c>) a placeholder-free string is a
    /// legitimate literal — a hard-coded address — so the engine accepts it and renders it unchanged.
    /// </summary>
    [Theory]
    [InlineData("ops@firma.sk")]
    [InlineData("a { b")]
    public void A_placeholder_free_literal_renders_unchanged(string source)
    {
        AlvoTemplate.Parse(source).Placeholders.ShouldBeEmpty();

        AlvoTemplate.Parse(source).Render(SampleEvent()).ShouldBe(source);
    }

    [Fact]
    public void Placeholders_are_the_trimmed_inner_text_in_source_order()
        => AlvoTemplate.Parse("Deal won: {{ new.title }} ({{new.amount}})").Placeholders
            .ShouldBe(["new.title", "new.amount"]);

    /// <summary>
    /// Rendering is fail-closed where refusing is impossible: at delivery there is nobody to report a
    /// refusal to, so a placeholder that apply-time validation should have refused throws rather than
    /// renders empty.
    /// </summary>
    [Fact]
    public void Rendering_a_placeholder_apply_time_would_have_refused_throws_rather_than_renders_empty()
        => Should.Throw<InvalidOperationException>(
            () => AlvoTemplate.Parse("{{@tenant.id}}").Render(SampleEvent()));

    private static EntitySchema Deals { get; } = new()
    {
        Name = "deals",
        Tenancy = TenancyMode.Scoped,
        Audit = true,
        Fields =
        [
            new FieldSchema { Name = "title", Type = FieldType.String },
            new FieldSchema { Name = "amount", Type = FieldType.Decimal },
            new FieldSchema { Name = "is_closed", Type = FieldType.Boolean },
        ],
    };

    private static EntitySchema GlobalDeals { get; } = Deals with { Tenancy = TenancyMode.Global };

    private static AlvoRecord Record(params (string Name, object? Value)[] fields)
        => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

    private static AlvoEvent SampleEvent(AlvoRecord? record = null) => new()
    {
        Id = Guid.Parse("019fc77e-be7b-72e8-b7fd-ffd6f6306e3e"),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.deals.updated",
        Time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero),
        Subject = "deals/3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        PartitionKey = "deals:3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        AuthType = AlvoEventAuthType.ApiKey,
        AuthId = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData
        {
            Record = record ?? Record(("title", "Big deal"), ("amount", 1200m)),
            OldRecord = Record(("title", "Small deal"), ("amount", 900m)),
            Changed = ["amount", "title"],
        },
    };
}
