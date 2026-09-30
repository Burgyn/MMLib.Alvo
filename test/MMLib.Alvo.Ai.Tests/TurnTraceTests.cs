using MMLib.Alvo.Ai.Internal;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>What a trace may hold and how big it may get (D44): no secret, no more than its cap, always its end.</summary>
public sealed class TurnTraceTests
{
    private const string QuotedRule = "'author_id == @user.id'";

    private static readonly TurnHeader _header = new("alvo-schema-assistant v5", "OpenAiCompatible", "m", "p");

    [Theory]
    [InlineData("key sk-live-0123456789abcdefghij", "sk-live-0123456789abcdefghij")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ", "ghp_0123456789abcdefghijABCDEFGHIJ")]
    [InlineData("hook xoxb-1234567890-abcdefghij", "xoxb-1234567890-abcdefghij")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    [InlineData("Server=db;User Id=alvo;Password=hunter2;", "hunter2")]
    [InlineData("key sk-proj-AbCdEfGhIjKlMnOpQrStUv_wx-yz12", "sk-proj-AbCdEfGhIjKlMnOpQrStUv_wx-yz12")]
    [InlineData("azure 0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("Authorization: Bearer abc.def-ghi_jkl.mno12345", "abc.def-ghi_jkl.mno12345")]
    [InlineData("Bearer 3f9a-77bc-1234-abcd-ef00", "3f9a-77bc-1234-abcd-ef00")]
    [InlineData("password=\"hunter2 x\"", "hunter2 x")]
    [InlineData("password: 'hunter2'", "hunter2")]
    [InlineData("client_secret=abcDEF123", "abcDEF123")]
    [InlineData("OPENAI_API_KEY=abc123def", "abc123def")]
    [InlineData("token=abcdef123", "abcdef123")]
    [InlineData("AccountName=a;AccountKey=abc+def/ghi==;EndpointSuffix=core", "abc+def/ghi==")]
    [InlineData("Endpoint=sb://x/;SharedAccessKeyName=r;SharedAccessKey=xyz+ab/c=", "xyz+ab/c=")]
    [InlineData("postgres://alvo:pass123@db:5432/app", "pass123")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    public void A_secret_like_value_is_redacted(string text, string secret)
    {
        var scrubbed = SecretScrub.Scrub(text);

        scrubbed.ShouldNotContain(secret);
        scrubbed.ShouldContain(SecretScrub.Redacted);
    }

    /// <summary>What a trace is for survives the scrub: pointers, CEL, and the framework's own refusals.</summary>
    [Theory]
    [InlineData("/entities/service_orders/fields/problem_description")]
    [InlineData("'admin' in @user.roles || assigned_user_id == @user.id")]
    [InlineData("A Rule expression must evaluate to a boolean; this expression evaluates to String.")]
    [InlineData("row.pin == 'Pa55w0rd-XYZ'")]
    [InlineData("api_token == @user.id")]
    [InlineData("/entities/tokens/fields/token")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("has(row.token) ? row.token : 'none'")]
    [InlineData("The expression: `token : x`.")]
    [InlineData("Basic information about the bike.")]
    public void A_descriptor_text_is_left_alone(string text) => SecretScrub.Scrub(text).ShouldBe(text);

    /// <summary>Every string of a JSON tree is scrubbed, keys and nesting kept (pre-flight M5: no mutation while walking).</summary>
    [Fact]
    public void A_json_tree_is_scrubbed_in_every_string_it_holds()
    {
        var node = new JsonObject
        {
            ["a"] = "key sk-live-0123456789abcdefghij",
            ["b"] = "Password=hunter2",
            ["c"] = new JsonArray("ok", new JsonObject { ["d"] = "AKIAIOSFODNN7EXAMPLE" }),
            ["e"] = 3,
        };

        var text = SecretScrub.Scrub(node)!.ToJsonString();

        text.ShouldNotContain("sk-live-0123456789abcdefghij");
        text.ShouldNotContain("hunter2");
        text.ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
        node["e"]!.GetValue<int>().ShouldBe(3);
        node["c"]![0]!.GetValue<string>().ShouldBe("ok");
    }

    /// <summary>A key is a string too (D44 "every string"): a secret-like key is redacted, its value kept.</summary>
    [Fact]
    public void A_secret_like_key_is_redacted_and_its_value_kept()
    {
        var node = new JsonObject { ["sk-live-0123456789abcdefghij"] = "value", ["plain"] = 1 };

        var text = SecretScrub.Scrub(node)!.ToJsonString();

        text.ShouldNotContain("sk-live-0123456789abcdefghij");
        text.ShouldContain(SecretScrub.Redacted);
        text.ShouldContain("\"value\"");
        text.ShouldContain("\"plain\":1");
    }

    /// <summary>The header's strings are scrubbed like the entries': a model or project name holding a key is redacted.</summary>
    [Fact]
    public void The_header_is_scrubbed_too()
    {
        var trace = TurnTrace.Json(TurnTrace.Of(new TurnHeader("v", "OpenAiCompatible", "sk-live-0123456789abcdefghij", "p"), [], TurnEnd.Answered));

        trace.ShouldNotContain("sk-live-0123456789abcdefghij");
    }

    /// <summary>A turn of forty 10 KB patches stays under the cap, and still says how it ended.</summary>
    [Fact]
    public void A_trace_never_exceeds_its_cap_and_keeps_its_header()
    {
        var note = string.Concat(Enumerable.Repeat("a long note ", 850));
        var calls = Enumerable.Range(1, 40).Select(round => new TracedCall(
            round, "c" + round.ToString(CultureInfo.InvariantCulture), "propose_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch(note), ["summary"] = "s" },
            """{"valid":false,"violations":[],"attemptsLeft":2}""")).ToList();

        var trace = TurnTrace.Of(_header, calls, TurnEnd.IterationCap);

        Encoding.UTF8.GetByteCount(TurnTrace.Json(trace)).ShouldBeLessThanOrEqualTo(TurnTrace.MaximumBytes);
        trace["end"]!.GetValue<string>().ShouldBe(TurnEnd.IterationCap);
        trace["format"]!.GetValue<string>().ShouldBe(TurnTrace.Format);
        trace["truncated"]!.GetValue<bool>().ShouldBeTrue();
        trace["callCount"]!.GetValue<int>().ShouldBe(40);
    }

    /// <summary>
    /// When even the entries without their operations cross the cap, whole calls are dropped, the header says how many,
    /// and the result — header included, measured as it is emitted — is still under the cap (pre-flight H2).
    /// </summary>
    [Fact]
    public void Calls_that_cannot_fit_are_dropped_and_counted_and_the_whole_stays_under_the_cap()
    {
        var refusal = string.Concat(Enumerable.Repeat("dlhá zamietnutá správa ", 300));
        var calls = Enumerable.Range(1, 40).Select(round => new TracedCall(
            round, "c" + round.ToString(CultureInfo.InvariantCulture), "check_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch("x") },
            Refused(refusal))).ToList();

        var trace = TurnTrace.Of(_header, calls, TurnEnd.Answered);

        Encoding.UTF8.GetByteCount(TurnTrace.Json(trace)).ShouldBeLessThanOrEqualTo(TurnTrace.MaximumBytes);
        trace["droppedCalls"]!.GetValue<int>().ShouldBeGreaterThan(0);
        (trace["calls"]!.AsArray().Count + trace["droppedCalls"]!.GetValue<int>()).ShouldBe(40);
    }

    /// <summary>A trace is measured and emitted with one serializer, so what was measured is what is sent (pre-flight H2, L9).</summary>
    [Fact]
    public void A_trace_is_emitted_as_it_was_measured_without_escaping_non_ascii()
    {
        var trace = TurnTrace.Of(_header, [new TracedCall(1, "c1", "check_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch("x") }, Refused("Dielňa <U Ťava>"))], TurnEnd.Answered);

        TurnTrace.Json(trace).ShouldContain("Dielňa <U Ťava>");
    }

    /// <summary>A secret the scrub lengthens cannot push a trace past its cap: each entry is scrubbed before it is measured.</summary>
    [Fact]
    public void Entries_are_scrubbed_before_they_are_measured()
    {
        var secrets = string.Join(' ', Enumerable.Repeat("Password=a", 2_000));
        var calls = Enumerable.Range(1, 12).Select(round => new TracedCall(
            round, "c" + round.ToString(CultureInfo.InvariantCulture), "check_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch("x") }, Refused(secrets))).ToList();

        var json = TurnTrace.Json(TurnTrace.Of(_header, calls, TurnEnd.Answered));

        Encoding.UTF8.GetByteCount(json).ShouldBeLessThanOrEqualTo(TurnTrace.MaximumBytes);
        json.ShouldNotContain("Password=a ");
    }

    /// <summary>The log line names where a patch wrote, never what it wrote (D44).</summary>
    [Fact]
    public void A_log_line_keeps_each_operations_path_and_drops_its_value()
    {
        var trace = TurnTrace.Of(_header, [new TracedCall(1, "c1", "propose_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch("private words") }, """{"valid":true}""")], TurnEnd.Answered);

        var line = TurnTrace.LogLine(trace["calls"]![0]!.AsObject()).ToJsonString();

        line.ShouldContain("/entities/bikes/fields/notes");
        line.ShouldNotContain("private words");
    }

    /// <summary>
    /// A log line keeps each violation's source, pointer, code and severity, and drops its message and fix: after D42 a
    /// refusal quotes the author's own source, and a log is where that text must never go (pre-flight H3).
    /// </summary>
    [Fact]
    public void A_log_line_carries_no_violation_text()
    {
        var trace = TurnTrace.Of(_header, [new TracedCall(1, "c1", "check_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch("x") }, Refused("The expression: `" + QuotedRule + "`."))], TurnEnd.Answered);

        var violation = TurnTrace.LogLine(trace["calls"]![0]!.AsObject())["result"]!["violations"]![0]!.AsObject();

        violation.Select(member => member.Key).Order(StringComparer.Ordinal).ShouldBe(["code", "pointer", "severity", "source"]);
        violation.ToJsonString().ShouldNotContain(QuotedRule);
    }

    /// <summary>Only what the trace is for is kept of a call's arguments: the summary as its length, any other key by name.</summary>
    [Fact]
    public void An_entry_keeps_the_patch_and_the_skill_and_reduces_everything_else()
    {
        var trace = TurnTrace.Of(_header, [new TracedCall(1, "c1", "propose_change",
            new Dictionary<string, object?>
            {
                ["baseRevision"] = 1, ["operations"] = JsonSerializer.SerializeToElement(Patch("x").GetRawText()),
                ["summary"] = "twelve chars", ["surprise"] = "what the model made up",
            }, """{"valid":true}""")], TurnEnd.Answered);

        var arguments = trace["calls"]![0]!["arguments"]!.AsObject();

        arguments["operations"]![0]!["path"]!.GetValue<string>().ShouldBe("/entities/bikes/fields/notes");
        arguments["summaryChars"]!.GetValue<int>().ShouldBe(12);
        arguments.ContainsKey("summary").ShouldBeFalse();
        arguments.ContainsKey("surprise").ShouldBeTrue();
        arguments.ToJsonString().ShouldNotContain("what the model made up");
    }

    private static string Refused(string message) => new JsonObject
    {
        ["valid"] = false,
        ["revision"] = 1,
        ["violations"] = new JsonArray(new JsonObject
        {
            ["source"] = "validation",
            ["pointer"] = "/entities/bikes/rules/get",
            ["message"] = message,
            ["fix"] = "Remove the outer quotes; the value is the expression itself: author_id == @user.id",
            ["code"] = null,
            ["severity"] = "error",
        }),
        ["attemptsLeft"] = 2,
    }.ToJsonString();

    private static JsonElement Patch(string description) => JsonSerializer.SerializeToElement(new JsonArray(new JsonObject
    {
        ["op"] = "add",
        ["path"] = "/entities/bikes/fields/notes",
        ["value"] = new JsonObject { ["type"] = "text", ["description"] = description },
    }));
}
