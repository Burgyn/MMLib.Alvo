using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Testing;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// Comparing <c>@user.id</c> with a ref to an entity other than <c>users</c> is a warning (spec §9, D51): the two are
/// never equal, so the rule is never true — yet the descriptor stays valid, because the check only warns.
/// </summary>
/// <remarks>Driven through the real <see cref="DescriptorValidator"/>, the way an apply reaches the check.</remarks>
public sealed class OwnerComparisonCheckTests
{
    private const string UpdateRule = "/entities/tasks/rules/update";
    private const string UpdateCondition = "/entities/tasks/hooks/beforeUpdate/0/condition";

    private static readonly DescriptorValidator _validator = new();

    /// <summary>Each rule or condition, whether it is still valid, and how many warnings it draws at its own pointer.</summary>
    [Theory]
    [InlineData("rule", "technician_id == @user.id", 1)]
    [InlineData("rule", "technician_id != @user.id", 1)]
    [InlineData("rule", "@user.id == technician_id", 1)]
    [InlineData("rule", "'admin' in @user.roles || technician_id == @user.id", 1)]
    [InlineData("rule", "assigned_user_id == @user.id", 0)]
    [InlineData("rule", "owner_uuid == @user.id", 0)]
    [InlineData("condition", "new.technician_id != @user.id", 1)]
    [InlineData("condition", "old.technician_id == @user.id", 1)]
    public void A_comparison_of_the_caller_with_another_entitys_ref_is_a_warning_and_nothing_else(string slot, string source, int warnings)
    {
        var result = _validator.Validate(Descriptor(slot, source));

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        Warnings(result, slot == "rule" ? UpdateRule : UpdateCondition).Count.ShouldBe(warnings);
    }

    /// <summary>
    /// A source that does not compile draws no warning; its compile error is the whole answer. A plain <c>string</c>
    /// compared with <c>@user.id</c> is one: the compiler already refuses it (<c>Cannot compare String to Uuid</c>).
    /// </summary>
    [Theory]
    [InlineData("technician_id == @user.id &&")]
    [InlineData("title == @user.id")]
    public void A_rule_that_does_not_compile_draws_no_warning_and_keeps_its_error(string source)
    {
        var result = _validator.Validate(Descriptor("rule", source));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Path == UpdateRule && error.Severity == DescriptorValidationSeverity.Error);
        result.Errors.ShouldNotContain(error => error.Severity == DescriptorValidationSeverity.Warning);
    }

    /// <summary>The warning names the field, the entity it holds an id of, and the fix.</summary>
    [Fact]
    public void The_warning_says_what_the_field_holds_and_how_to_compare_the_caller()
    {
        var warning = Warnings(_validator.Validate(Descriptor("rule", "technician_id == @user.id")), UpdateRule).ShouldHaveSingleItem();

        warning.Path.ShouldBe(UpdateRule);
        warning.Severity.ShouldBe(DescriptorValidationSeverity.Warning);
        warning.Message.ShouldStartWith("'technician_id' holds a technicians id, and @user.id is a user id");
        warning.FixSuggestion.ShouldNotBeNull().ShouldContain("refs 'users'");
    }

    /// <summary>Every shipped example draws no such warning: the check does not move what the examples mean.</summary>
    [Fact]
    public void No_shipped_example_draws_a_warning()
    {
        var examples = Directory.EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "examples"), "*.alvo.json", SearchOption.AllDirectories).ToList();

        examples.ShouldNotBeEmpty();
        examples.ShouldAllBe(path => !_validator.Validate(File.ReadAllText(path)).Errors.Any(error => error.Severity == DescriptorValidationSeverity.Warning));
    }

    private static List<DescriptorValidationError> Warnings(DescriptorValidationResult result, string pointer) =>
        [.. result.Errors.Where(error => error.Severity == DescriptorValidationSeverity.Warning && error.Path == pointer)];

    /// <summary>
    /// <c>technicians</c> (with a <c>uuid</c> user id) and <c>tasks</c>, whose update rule — or before-update condition —
    /// is <paramref name="source"/>.
    /// </summary>
    private static string Descriptor(string slot, string source)
    {
        var tasks = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string" },
                ["owner_uuid"] = new JsonObject { ["type"] = "uuid" },
                ["technician_id"] = new JsonObject { ["type"] = "ref", ["entity"] = "technicians" },
                ["assigned_user_id"] = new JsonObject { ["type"] = "ref", ["entity"] = "users" },
            },
            ["rules"] = new JsonObject { ["update"] = slot == "rule" ? source : "true" },
        };
        if (slot == "condition")
        {
            tasks["hooks"] = new JsonObject
            {
                ["beforeUpdate"] = new JsonArray(new JsonObject { ["condition"] = source, ["action"] = new JsonObject { ["reject"] = "No." } }),
            };
        }

        return new JsonObject
        {
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "demo",
            ["entities"] = new JsonObject
            {
                ["technicians"] = new JsonObject { ["fields"] = new JsonObject { ["user_id"] = new JsonObject { ["type"] = "uuid" } } },
                ["tasks"] = tasks,
            },
        }.ToJsonString();
    }
}
