using CsCheck;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

using System.Text.Json;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// A <c>webhook.payload</c> template renders JSON by construction: a value inside a JSON string is that
/// string's escaped content, a value outside one is exactly one JSON value, and a row can never add, remove or
/// close a member, an element or a string.
/// </summary>
/// <remarks>
/// Object-shaped templates appear here although <see cref="JsonataSlot"/> refuses a bare brace in the payload
/// slot today: the renderer is the defence, the classifier is not, and a later PR that relaxes the classifier
/// must find the renderer already safe for objects.
/// </remarks>
public class JsonPayloadTests
{
    private const string MemberForgery = "\",\"admin\":true,\"x\":\"";

    [Fact]
    public void A_quoted_value_that_tries_to_close_its_string_and_add_a_member_stays_one_string()
    {
        var body = Rendered("{\"t\":\"{{new.title}}\"}", ("title", MemberForgery));

        var root = Parsed(body);
        root.EnumerateObject().Select(member => member.Name).ShouldBe(["t"]);
        root.GetProperty("t").GetString().ShouldBe(MemberForgery);
    }

    [Fact]
    public void A_bare_text_value_that_tries_to_add_a_member_is_written_as_one_json_string()
    {
        const string Forgery = "1, \"admin\": true";

        var root = Parsed(Rendered("{\"n\": {{new.title}}}", ("title", Forgery)));

        root.EnumerateObject().Select(member => member.Name).ShouldBe(["n"]);
        root.GetProperty("n").GetString().ShouldBe(Forgery);
    }

    /// <summary>
    /// The case the no-bare-brace classifier left open and <c>events.md</c> recorded: <c>[</c> is not a brace,
    /// so an array payload was accepted and a value carrying <c>", "</c> forged an element.
    /// </summary>
    [Fact]
    public void A_quoted_value_that_tries_to_forge_an_array_element_stays_one_element()
    {
        var root = Parsed(Rendered("[\"{{new.title}}\", \"{{new.amount}}\"]", ("title", "a\", \"forged"), ("amount", 1m)));

        root.GetArrayLength().ShouldBe(2);
        root[0].GetString().ShouldBe("a\", \"forged");
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{}")]
    [InlineData("1, 2")]
    [InlineData("true")]
    [InlineData("null")]
    public void A_bare_text_value_shaped_like_json_is_still_a_string(string value)
    {
        var root = Parsed(Rendered("[{{new.title}}]", ("title", value)));

        root.GetArrayLength().ShouldBe(1);
        root[0].GetString().ShouldBe(value);
    }

    [Fact]
    public void Bare_numbers_booleans_and_nulls_render_as_json_values_in_the_envelopes_spelling()
        => Rendered(
                "{\"n\": {{new.amount}}, \"c\": {{new.is_closed}}, \"x\": {{new.title}}}",
                ("amount", 1200.50m), ("is_closed", true), ("title", null))
            .ShouldBe("{\"n\": 1200.50, \"c\": true, \"x\": null}");

    /// <summary>A null inside quotes is the empty string, as it is in every plain-text template.</summary>
    [Fact]
    public void A_quoted_null_renders_as_the_empty_string()
        => Rendered("[\"{{new.title}}\"]", ("title", null)).ShouldBe("[\"\"]");

    [Fact]
    public void A_normal_value_renders_unchanged_inside_its_quotes()
        => Rendered("[\"{{new.title}}\"]", ("title", "Big deal")).ShouldBe("[\"Big deal\"]");

    /// <summary>
    /// A whole-body placeholder used to post <c>Big deal</c> under <c>application/json</c>, which is not JSON;
    /// it now posts the JSON string <c>"Big deal"</c>.
    /// </summary>
    [Fact]
    public void A_whole_body_placeholder_renders_a_json_string_rather_than_raw_text()
        => Rendered("{{new.title}}", ("title", "Big deal")).ShouldBe("\"Big deal\"");

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\tand\u0000nul")]
    [InlineData("back\\slash and \"quote\"")]
    [InlineData("</script><script>alert(1)</script>")]
    [InlineData("žluťoučký kůň — 😀")]
    public void Control_quote_html_and_non_ascii_characters_round_trip_as_data_in_both_positions(string value)
    {
        var root = Parsed(Rendered("[\"{{new.title}}\", {{new.title}}]", ("title", value)));

        root.GetArrayLength().ShouldBe(2);
        root[0].GetString().ShouldBe(value);
        root[1].GetString().ShouldBe(value);
    }

    /// <summary>
    /// For any text value, in a quoted and in a bare position, the body parses to the author's shape with the
    /// value as one string leaf — the property the two positions' encodings exist to guarantee.
    /// </summary>
    [Fact]
    public void For_any_text_value_the_body_keeps_the_authors_shape_and_carries_the_value_as_one_leaf()
    {
        long rendered = 0;

        _values.Sample(value =>
        {
            var root = Parsed(Rendered("{\"q\": \"<{{new.title}}>\", \"b\": [{{new.title}}]}", ("title", value)));
            Interlocked.Increment(ref rendered);

            return root.EnumerateObject().Count() == 2
                && root.GetProperty("q").GetString() == $"<{value}>"
                && root.GetProperty("b").GetArrayLength() == 1
                && root.GetProperty("b")[0].GetString() == value;
        },
        iter: 5_000);

        rendered.ShouldBe(5_000);
    }

    /// <summary>
    /// A lone surrogate is not valid text, so it cannot round-trip — but it still cannot become structure: the
    /// body is refused, or it parses to the author's shape.
    /// </summary>
    [Fact]
    public void A_lone_surrogate_is_refused_or_rendered_without_changing_the_shape()
    {
        var value = $"a{(char)0xD800}\"]";
        string? body = null;

        var refused = Record.Exception(() => body = TryRendered("[\"{{new.title}}\"]", ("title", value)));

        if (refused is null && body is not null)
        {
            Parsed(body).GetArrayLength().ShouldBe(1);
        }
    }

    [Theory]
    [InlineData("{{new.title}}")]
    [InlineData("[\"{{new.title}}\", {{new.amount}}]")]
    [InlineData("{\"a\": {{new.amount}}, \"b\": \"x {{new.title}} y\"}")]
    [InlineData("\"{{new.title}}\"")]
    public void A_template_that_is_json_around_its_placeholders_validates(string source)
        => JsonPayload.TryValidate(AlvoTemplate.Parse(source), out _).ShouldBeTrue();

    [Theory]
    [InlineData("Deal won: {{new.title}}")]
    [InlineData("[{{new.title}} {{new.amount}}]")]
    [InlineData("[\"{{new.title}}\"")]
    [InlineData("{{new.title}}, {{new.amount}}")]
    public void A_template_that_is_not_json_around_its_placeholders_is_refused(string source)
    {
        JsonPayload.TryValidate(AlvoTemplate.Parse(source), out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain("not a JSON document");
    }

    /// <summary>
    /// A placeholder straight after a backslash would let the value's first character pair with it — a value
    /// starting with a backslash would then escape the author's closing quote.
    /// </summary>
    [Fact]
    public void A_placeholder_straight_after_a_backslash_inside_a_string_is_refused_by_name()
    {
        JsonPayload.TryValidate(AlvoTemplate.Parse("[\"\\{{new.title}}\"]"), out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain("backslash");
    }

    [Fact]
    public void An_escaped_quote_in_the_authors_text_does_not_end_the_string()
        => Rendered("[\"say \\\"{{new.title}}\\\"\"]", ("title", "\"hi\""))
            .ShouldBe("[\"say \\\"\\u0022hi\\u0022\\\"\"]");

    private static readonly Gen<string> _values =
        Gen.Char.Where(character => !char.IsSurrogate(character)).Array[0, 24].Select(characters => new string(characters));

    private static string Rendered(string source, params (string Field, object? Value)[] values) =>
        TryRendered(source, values) ?? throw new ShouldAssertException($"'{source}' did not render JSON.");

    private static string? TryRendered(string source, params (string Field, object? Value)[] values)
    {
        var template = AlvoTemplate.Parse(source);
        JsonPayload.TryValidate(template, out var refusal).ShouldBeTrue(refusal);

        return JsonPayload.TryRender(template, SampleEvent(values), out var body) ? body : null;
    }

    private static JsonElement Parsed(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static AlvoEvent SampleEvent((string Field, object? Value)[] values) => new()
    {
        Id = Guid.Parse("019fc77e-be7b-72e8-b7fd-ffd6f6306e3e"),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.deals.updated",
        Time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero),
        Subject = "deals/3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        PartitionKey = "deals:3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData
        {
            Record = new AlvoRecord(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal)),
            Changed = ["title"],
        },
    };
}
