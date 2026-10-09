using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The guided condition's text, written canonically (spec §7.2).</summary>
public class ConditionTextTests
{
    [Theory]
    [InlineData(ConditionOperator.Is, RowImage.New, "status", ConditionFieldKind.Choice, "ready", "new.status == 'ready'")]
    [InlineData(ConditionOperator.IsNot, RowImage.New, "title", ConditionFieldKind.Text, "draft", "new.title != 'draft'")]
    [InlineData(ConditionOperator.IsNotOrEmpty, RowImage.Old, "note", ConditionFieldKind.Text, "x", "!(old.note == 'x')")]
    [InlineData(ConditionOperator.Less, RowImage.New, "quantity", ConditionFieldKind.Number, "4.5", "new.quantity < 4.5")]
    [InlineData(ConditionOperator.LessOrEqual, RowImage.New, "quantity", ConditionFieldKind.Number, "0", "new.quantity <= 0")]
    [InlineData(ConditionOperator.Greater, RowImage.Old, "quantity", ConditionFieldKind.Number, "12", "old.quantity > 12")]
    [InlineData(ConditionOperator.GreaterOrEqual, RowImage.New, "quantity", ConditionFieldKind.Number, "1", "new.quantity >= 1")]
    [InlineData(ConditionOperator.IsTrue, RowImage.New, "notify_customer", ConditionFieldKind.Flag, "", "new.notify_customer == true")]
    [InlineData(ConditionOperator.IsFalse, RowImage.Old, "notify_customer", ConditionFieldKind.Flag, "", "old.notify_customer == false")]
    [InlineData(ConditionOperator.HasValue, RowImage.New, "note", ConditionFieldKind.Text, "", "has(new.note)")]
    [InlineData(ConditionOperator.IsEmpty, RowImage.Old, "due", ConditionFieldKind.Moment, "", "!has(old.due)")]
    [InlineData(ConditionOperator.Changed, RowImage.New, "status", ConditionFieldKind.Choice, "", "changed(status)")]
    [InlineData(ConditionOperator.IsTheWriter, RowImage.New, "owner_id", ConditionFieldKind.Identity, "", "new.owner_id == @user.id")]
    [InlineData(ConditionOperator.HasRole, RowImage.New, "@user", ConditionFieldKind.Text, "manager", "'manager' in @user.roles")]
    [InlineData(ConditionOperator.LacksRole, RowImage.New, "@user", ConditionFieldKind.Text, "manager", "!('manager' in @user.roles)")]
    public void A_row_is_written_in_its_canonical_cel(object relation, object image, string field, object kind, string value, string cel)
        => ConditionText.Row(new ConditionRow((ConditionOperator)relation, (RowImage)image, field, (ConditionFieldKind)kind, value)).ShouldBe(cel);

    [Fact]
    public void Every_relation_of_the_table_is_written_by_its_format_alone()
    {
        foreach (var spec in ConditionTable.Rows)
        {
            var row = new ConditionRow(spec.Operator, RowImage.New, "f", ConditionFieldKind.Text, spec.Operand == OperandKind.None ? string.Empty : "v");

            ConditionText.Row(row).ShouldBe(
                spec.Format.Replace("{f}", "new.f", StringComparison.Ordinal).Replace("{n}", "f", StringComparison.Ordinal)
                    .Replace("{v}", "'v'", StringComparison.Ordinal).Replace("{r}", "'v'", StringComparison.Ordinal),
                spec.Operator.ToString());
        }
    }

    [Fact]
    public void A_value_is_quoted_with_only_the_escapes_the_lexer_reads()
        => ConditionText.Quote("it's \\ \"ok\"\n\t\r").ShouldBe("'it\\'s \\\\ \"ok\"\\n\\t\\r'");

    [Theory]
    [InlineData("čaj")]
    [InlineData("中文")]
    [InlineData("naïve café")]
    [InlineData("\U0001F600")]
    public void A_value_beyond_ascii_is_written_as_typed_since_the_lexer_reads_every_character_raw(string value)
        => ConditionText.Quote(value).ShouldBe($"'{value}'");

    [Theory]
    [InlineData("{v}")]
    [InlineData("{r}")]
    [InlineData("{f} && {n}")]
    public void A_value_that_spells_a_placeholder_is_written_once_and_never_substituted_again(string value)
    {
        ConditionText.Row(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, value))
            .ShouldBe($"new.title == '{value}'");
        ConditionText.Row(new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, value))
            .ShouldBe($"'{value}' in @user.roles");
    }

    [Fact]
    public void A_number_is_written_bare_and_every_other_literal_quoted()
    {
        ConditionText.Row(new ConditionRow(ConditionOperator.Is, RowImage.New, "quantity", ConditionFieldKind.Number, "3")).ShouldBe("new.quantity == 3");
        ConditionText.Row(new ConditionRow(ConditionOperator.Is, RowImage.New, "code", ConditionFieldKind.Text, "3")).ShouldBe("new.code == '3'");
        ConditionText.Row(new ConditionRow(ConditionOperator.Is, RowImage.New, "status", ConditionFieldKind.Choice, "3")).ShouldBe("new.status == '3'");
    }

    [Theory]
    [InlineData("0 || true", "new.quantity < '0 || true'")]
    [InlineData("-5", "new.quantity < '-5'")]
    [InlineData("1e3", "new.quantity < '1e3'")]
    [InlineData("it's", "new.quantity < 'it\\'s'")]
    [InlineData("12\n", "new.quantity < '12\\n'")]
    public void A_number_box_holding_anything_but_a_number_is_written_as_a_quoted_literal_never_as_syntax(string value, string cel)
        => ConditionText.Row(new ConditionRow(ConditionOperator.Less, RowImage.New, "quantity", ConditionFieldKind.Number, value)).ShouldBe(cel);

    [Fact]
    public void A_row_whose_value_is_null_is_written_and_judged_as_empty()
    {
        var literal = new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, null!);
        var role = new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, "manager") with { Value = null! };

        literal.Value.ShouldBe(string.Empty);
        ConditionText.Row(literal).ShouldBe("new.title == ''");
        ConditionText.Refusal(Single(role)).ShouldBe("Choose a role.");
        ConditionText.Normalize(Single(literal)).Rows.Single().Value.ShouldBe(string.Empty);
    }

    [Fact]
    public void Rows_are_joined_by_all_or_any()
    {
        ConditionRow[] rows =
        [
            new(ConditionOperator.Is, RowImage.New, "status", ConditionFieldKind.Choice, "ready"),
            new(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, "manager"),
        ];

        ConditionText.Generate(new GuidedCondition(true, rows)).ShouldBe("new.status == 'ready' && 'manager' in @user.roles");
        ConditionText.Generate(new GuidedCondition(false, rows)).ShouldBe("new.status == 'ready' || 'manager' in @user.roles");
    }

    [Fact]
    public void No_rows_write_no_condition()
        => ConditionText.Generate(GuidedCondition.Empty).ShouldBeEmpty();

    [Theory]
    [InlineData("-5", "negative number")]
    [InlineData("1e3", "not a number")]
    [InlineData("12.", "not a number")]
    [InlineData(".5", "not a number")]
    [InlineData("007", "not a number")]
    [InlineData("", "not a number")]
    [InlineData(" 3", "not a number")]
    [InlineData("12\n", "not a number")]
    public void A_number_a_condition_cannot_hold_is_refused(string value, string says)
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "quantity", ConditionFieldKind.Number, value)))
            .ShouldNotBeNull().ShouldContain(says);

    [Theory]
    [InlineData("0")]
    [InlineData("12")]
    [InlineData("4.5")]
    [InlineData("0.01")]
    public void A_number_a_condition_can_hold_is_accepted(string value)
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "quantity", ConditionFieldKind.Number, value))).ShouldBeNull();

    /// <summary>The core reads a whole number as a <c>long</c> and a decimal as a <c>decimal</c> (<c>CelParser</c>).</summary>
    /// <param name="value">The value typed.</param>
    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("99999999999999999999999999999")]
    [InlineData("79228162514264337593543950336.5")]
    public void A_number_beyond_what_the_core_reads_is_refused(string value)
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "quantity", ConditionFieldKind.Number, value)))
            .ShouldNotBeNull().ShouldContain("too large");

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("79228162514264337593543950335.0")]
    public void The_largest_number_the_core_reads_is_accepted(string value)
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "quantity", ConditionFieldKind.Number, value))).ShouldBeNull();

    [Fact]
    public void A_condition_longer_than_a_condition_may_be_is_refused()
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, new string('x', 2000))))
            .ShouldNotBeNull().ShouldContain("2000");

    [Fact]
    public void A_condition_exactly_as_long_as_a_condition_may_be_is_accepted()
    {
        // "new.title == '" + value + "'" is 15 characters around the value.
        var row = new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, new string('x', ConditionTable.MaxConditionLength - 15));

        ConditionText.Row(row).Length.ShouldBe(ConditionTable.MaxConditionLength);
        ConditionText.Refusal(Single(row)).ShouldBeNull();
    }

    [Theory]
    [InlineData("bell\u0007")]
    [InlineData("nul\0")]
    [InlineData("del\u007f")]
    [InlineData("next\u0085line")]
    public void A_value_with_a_control_character_the_lexer_cannot_spell_is_refused(string value)
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, value)))
            .ShouldNotBeNull().ShouldContain("text mode");

    /// <summary>
    /// An invisible formatting character would make the stored condition read differently from what it does (Trojan
    /// Source, CVE-2021-42574), so the guided form refuses it in a value and in a role.
    /// </summary>
    /// <param name="value">The value typed.</param>
    [Theory]
    [InlineData("admin‮' || true")]
    [InlineData("⁦x⁩")]
    [InlineData("zero​width")]
    [InlineData("soft­hyphen")]
    [InlineData("﻿bom")]
    [InlineData("x\U000E0041")]
    public void A_value_with_an_invisible_formatting_character_is_refused(string value)
    {
        ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, value)))
            .ShouldNotBeNull().ShouldContain("text mode");
        ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, value)))
            .ShouldNotBeNull().ShouldContain("text mode");
    }

    /// <summary>
    /// The zero-width non-joiner and joiner are Format characters that Persian and Indic text and emoji sequences need; they
    /// cannot reorder what is shown, so they are the two the guided form accepts.
    /// </summary>
    /// <param name="value">The value typed.</param>
    [Theory]
    [InlineData("می‌خواهم")]
    [InlineData("क्‍ष")]
    [InlineData("👨‍👩‍👧")]
    public void A_value_with_a_joiner_or_a_non_joiner_is_accepted(string value)
    {
        ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, value))).ShouldBeNull();
        ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, value))).ShouldBeNull();
    }

    [Fact]
    public void A_role_with_a_control_character_is_refused_as_a_value_is()
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.LacksRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, "bell\u0007")))
            .ShouldNotBeNull().ShouldContain("text mode");

    [Fact]
    public void A_value_with_a_line_break_or_a_tab_is_accepted_because_the_lexer_has_an_escape_for_it()
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, "a\nb\tc\r"))).ShouldBeNull();

    [Fact]
    public void A_role_row_with_no_role_is_refused()
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, "")))
            .ShouldBe("Choose a role.");

    [Fact]
    public void The_first_refused_row_is_the_one_named()
    {
        GuidedCondition condition = new(true,
        [
            new(ConditionOperator.Is, RowImage.New, "title", ConditionFieldKind.Text, "fine"),
            new(ConditionOperator.Is, RowImage.New, "quantity", ConditionFieldKind.Number, "-1"),
            new(ConditionOperator.LacksRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, ""),
        ]);

        ConditionText.Refusal(condition).ShouldNotBeNull().ShouldContain("'-1'");
    }

    [Fact]
    public void A_single_row_is_normalized_to_all_and_an_imageless_row_to_new()
    {
        GuidedCondition typed = new(false,
        [
            new(ConditionOperator.Changed, RowImage.Old, "status", ConditionFieldKind.Choice, "stale"),
        ]);

        ConditionText.Normalize(typed).ShouldBe(new GuidedCondition(true,
        [
            new(ConditionOperator.Changed, RowImage.New, "status", ConditionFieldKind.Choice, ""),
        ]));
    }

    [Fact]
    public void A_role_row_is_normalized_to_text_on_new_and_keeps_its_role()
        => ConditionText.Normalize(Single(new ConditionRow(ConditionOperator.HasRole, RowImage.Old, ConditionTable.Writer, ConditionFieldKind.Number, "manager")))
            .Rows.Single().ShouldBe(new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, "manager"));

    [Fact]
    public void Two_rows_keep_their_joiner_and_a_literal_row_its_image_and_value()
    {
        GuidedCondition any = new(false,
        [
            new(ConditionOperator.Is, RowImage.Old, "status", ConditionFieldKind.Choice, "ready"),
            new(ConditionOperator.HasValue, RowImage.Old, "note", ConditionFieldKind.Text, ""),
        ]);

        ConditionText.Normalize(any).ShouldBe(any);
    }

    [Fact]
    public void Conditions_are_equal_by_their_joiner_and_rows_not_by_the_list_holding_them()
    {
        ConditionRow row = new(ConditionOperator.Is, RowImage.New, "status", ConditionFieldKind.Choice, "ready");

        new GuidedCondition(true, [row]).ShouldBe(new GuidedCondition(true, new List<ConditionRow> { row with { } }));
        new GuidedCondition(true, [row]).ShouldNotBe(new GuidedCondition(false, [row]));
        new GuidedCondition(true, [row]).ShouldNotBe(new GuidedCondition(true, [row with { Value = "done" }]));
        new GuidedCondition(true, [row]).GetHashCode().ShouldBe(new GuidedCondition(true, [row with { }]).GetHashCode());
    }

    [Fact]
    public void A_scope_offers_the_stored_fields_and_leaves_out_derived_ones()
    {
        var scope = ConditionScope.From(Entity(), ["manager"]);

        scope.Fields.Select(field => field.Name).ShouldBe(["status", "note", "quantity"]);
        scope.Field("status").ShouldBe(new ConditionField("status", ConditionFieldKind.Choice, false, ["open", "done"]), new FieldEquality());
        scope.Field("note")!.Nullable.ShouldBeTrue();
        scope.Field("note")!.Values.ShouldBeEmpty();
        scope.Field("total").ShouldBeNull();
        scope.Field("Status").ShouldBeNull();
        scope.Roles.ShouldBe(["manager"]);
    }

    [Fact]
    public void No_entity_is_a_scope_of_no_fields()
        => ConditionScope.From(null, []).Fields.ShouldBeEmpty();

    private static GuidedCondition Single(ConditionRow row) => new(true, [row]);

    private static EntitySchema Entity() => new()
    {
        Name = "orders",
        Fields =
        [
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "done"] },
            new FieldSchema { Name = "note", Type = FieldType.Text, Nullable = true },
            new FieldSchema { Name = "quantity", Type = FieldType.Integer },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, ComputedExpression = "quantity * 2" },
            new FieldSchema
            {
                Name = "lines",
                Type = FieldType.Integer,
                Rollup = new RollupSchema { From = "lines", Op = RollupOperation.Count, Via = "order_id" },
            },
        ],
    };

    private sealed class FieldEquality : IEqualityComparer<ConditionField?>
    {
        public bool Equals(ConditionField? x, ConditionField? y)
            => x is not null && y is not null && x.Name == y.Name && x.Kind == y.Kind && x.Nullable == y.Nullable && x.Values.SequenceEqual(y.Values);

        public int GetHashCode(ConditionField? obj) => obj?.Name.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
