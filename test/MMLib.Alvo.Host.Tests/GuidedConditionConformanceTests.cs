using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>Everything the guided condition offers, the apply accepts; what it withholds before the commit, the apply refuses.</b>
/// </summary>
/// <remarks>
/// <c>ConditionTable</c> is the one source the form, the generator and the recognizer read (spec §7.1). This compiles every
/// cell — point × field kind × nullability × relation × image — through the real <c>DescriptorValidator</c>, the code
/// path apply runs (the hook compilers' phase and envelope rules included, which <c>CelTypeChecker</c> alone would miss),
/// so the table cannot drift from the core without this failing (spec §7.4, acceptance criterion 4).
/// </remarks>
public sealed class GuidedConditionConformanceTests
{
    private static readonly (string Name, string Declaration)[] _fields =
    [
        ("text_req", """{"type":"string","required":true}"""),
        ("text_opt", """{"type":"text"}"""),
        ("choice_req", """{"type":"enum","values":["open","it's"],"required":true}"""),
        ("choice_opt", """{"type":"enum","values":["open","it's"]}"""),
        ("number_req", """{"type":"integer","required":true}"""),
        ("number_opt", """{"type":"decimal","precision":12,"scale":2}"""),
        ("flag_req", """{"type":"boolean","required":true}"""),
        ("flag_opt", """{"type":"boolean"}"""),
        ("moment_req", """{"type":"datetime","required":true}"""),
        ("moment_opt", """{"type":"date"}"""),
        ("identity_req", """{"type":"uuid","required":true}"""),
        ("identity_opt", """{"type":"uuid"}"""),
        ("json_opt", """{"type":"json"}"""),
    ];

    /// <summary>The probe is a valid descriptor at every point, so an error the facts below see is the condition's own.</summary>
    /// <param name="point">The hook point.</param>
    [Theory]
    [MemberData(nameof(Points))]
    public void The_probe_is_valid_with_a_condition_that_always_holds(string point)
    {
        new DescriptorValidator().Validate(Probe(point, "true")).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)
            .Select(error => $"{error.Path}: {error.Message}")
            .ShouldBeEmpty();
        Scope(point).Fields.Count.ShouldBe(_fields.Length, "every probe field is one the condition may name");
    }

    [Fact]
    public void Every_combination_the_form_offers_is_accepted_by_apply()
    {
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var point in HookBuilder.Points)
        {
            foreach (var row in Offered(point))
            {
                var condition = ConditionText.Generate(new GuidedCondition(true, [row]));
                checkedCount++;
                if (ErrorsAt(point, condition) is [var first, ..])
                {
                    failures.Add($"{point}: {condition} — {first.Message}");
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{checkedCount} guided conditions checked against the real validator.");
        checkedCount.ShouldBeGreaterThan(200, "the probe covers every kind at every point");
        failures.ShouldBeEmpty();
    }

    /// <summary>
    /// The four refusal classes of spec §7.4: <c>old.</c> before a create, <c>new.</c> before a delete, <c>changed</c> at a
    /// create or delete before-point, and a role row at an after-point.
    /// </summary>
    /// <param name="point">The hook point.</param>
    /// <param name="condition">The condition the table withholds there.</param>
    /// <param name="refusal">What the core's refusal names — its class, so an unrelated error cannot stand in for it.</param>
    [Theory]
    [InlineData("beforeCreate", "old.text_opt == 'x'", "'old.<field>'")]
    [InlineData("beforeDelete", "new.text_opt == 'x'", "'new.<field>'")]
    [InlineData("beforeCreate", "changed(text_opt)", "'changed(<field>)'")]
    [InlineData("beforeDelete", "changed(text_opt)", "'changed(<field>)'")]
    [InlineData("afterCreate", "'clerk' in @user.roles", "'@user.roles'")]
    [InlineData("afterUpdate", "!('clerk' in @user.roles)", "'@user.roles'")]
    public void What_the_table_withholds_before_the_commit_or_from_an_envelope_apply_refuses(string point, string condition, string refusal)
    {
        ErrorsAt(point, condition).ShouldContain(error => error.Message.Contains(refusal, StringComparison.Ordinal), $"{condition} at {point}");
        ConditionText.Recognize(condition, point, Scope(point)).ShouldBeNull("the form never offers it");
    }

    /// <summary>
    /// Where the table is narrower than apply — recorded, so the spec §7.1 "unverified" becomes a measured fact. If apply
    /// starts refusing these, move them to the theory above and drop the sentence from the spec.
    /// </summary>
    /// <param name="point">The hook point.</param>
    /// <param name="condition">The condition the table withholds there.</param>
    [Theory]
    [InlineData("afterCreate", "old.text_opt == 'x'")]
    [InlineData("afterDelete", "new.text_opt == 'x'")]
    public void What_the_table_withholds_after_the_commit_apply_still_accepts(string point, string condition)
    {
        ErrorsAt(point, condition).ShouldBeEmpty($"{condition} at {point}");
        ConditionText.Recognize(condition, point, Scope(point)).ShouldBeNull("the form never offers an image the point lacks");
    }

    public static TheoryData<string> Points() => [.. HookBuilder.Points];

    private static IEnumerable<ConditionRow> Offered(string point)
    {
        var scope = Scope(point);
        foreach (var field in scope.Fields)
        {
            foreach (var spec in ConditionTable.For(point, field.Kind, field.Nullable))
            {
                foreach (var row in Rows(spec, field, point))
                {
                    yield return row;
                }
            }
        }

        foreach (var spec in ConditionTable.ForWriter(point))
        {
            yield return new ConditionRow(spec.Operator, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, "clerk");
        }
    }

    private static IEnumerable<ConditionRow> Rows(OperatorSpec spec, ConditionField field, string point)
    {
        IReadOnlyList<RowImage> images = spec.Operator == ConditionOperator.Changed ? [RowImage.New] : ConditionTable.ImagesAt(point);
        foreach (var image in images)
        {
            foreach (var value in Samples(spec, field))
            {
                yield return new ConditionRow(spec.Operator, image, field.Name, field.Kind, value);
            }
        }
    }

    private static string[] Samples(OperatorSpec spec, ConditionField field) => spec.Operand != OperandKind.Literal
        ? [string.Empty]
        : field.Kind switch
        {
            ConditionFieldKind.Number => ["12", "4.5"],
            ConditionFieldKind.Choice => ["it's"],
            _ => ["it's \\ \"quoted\"\n"],
        };

    private static ConditionScope Scope(string point)
        => ConditionScope.From(PendingSchema.Read(Probe(point, "true"), "probe"), ["clerk"]);

    private static List<DescriptorValidationError> ErrorsAt(string point, string condition)
    {
        var prefix = $"/entities/probe/hooks/{point}/0";
        return [.. new DescriptorValidator().Validate(Probe(point, condition)).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)
            .Where(error => error.Path.StartsWith(prefix, StringComparison.Ordinal) || error.Path.StartsWith("#" + prefix, StringComparison.Ordinal))];
    }

    private static string Probe(string point, string condition)
    {
        var fields = new JsonObject();
        foreach (var (name, declaration) in _fields)
        {
            fields[name] = JsonNode.Parse(declaration);
        }

        JsonNode action = HookBuilder.IsBefore(point)
            ? new JsonObject { ["reject"] = "No." }
            : new JsonObject { ["type"] = "webhook", ["endpoint"] = "probe-desk" };
        return new JsonObject
        {
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "probe",
            ["auth"] = new JsonObject { ["roles"] = new JsonArray("clerk") },
            ["webhooks"] = JsonNode.Parse("""{"endpoints":{"probe-desk":{"url":"https://example.com/hook","secretRef":"probe-desk-key"}}}"""),
            ["entities"] = new JsonObject
            {
                ["probe"] = new JsonObject
                {
                    ["fields"] = fields,
                    ["hooks"] = new JsonObject { [point] = new JsonArray(new JsonObject { ["condition"] = condition, ["action"] = action }) },
                },
            },
        }.ToJsonString();
    }
}
