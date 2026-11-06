using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Expressions;

using System.Text.Json.Nodes;

using static MMLib.Alvo.Tests.Rules.RelationDescriptors;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// A lookup check compiles to one forward relation over its lookup whose body is the whole condition, so negation inside
/// it stays inside the <c>EXISTS</c>; it reads no caller, must read its lookup, and follows a ref to a declared physical
/// entity only. Refusals are driven through the real <see cref="DescriptorValidator"/>, the way an apply reaches them.
/// </summary>
public sealed class CheckCompilerTests
{
    private const string Available = "This product is not available in that quantity.";

    private static readonly DescriptorValidator _validator = new();

    [Fact]
    public void A_check_compiles_to_one_forward_relation_whose_body_is_the_whole_expression()
    {
        var check = CompiledCheck("product_id.active && quantity <= product_id.stock");

        check.Lookup.ShouldBe("product_id");
        check.Message.ShouldBe(Available);
        var relation = check.Relation;
        relation.Kind.ShouldBe(CelRelationKind.Forward);
        relation.Related.Name.ShouldBe("products");
        (relation.RelatedKey, relation.OuterKey, relation.Via).ShouldBe(("id", "product_id", "product_id"));
        relation.Scope.ShouldBeNull();
        var and = relation.Body.ShouldBeOfType<CelBinary>();
        and.Operator.ShouldBe(CelBinaryOperator.And);
        and.Left.ShouldBeOfType<CelFieldRef>().FieldName.ShouldBe("active");
        var stock = and.Right.ShouldBeOfType<CelBinary>();
        stock.Left.ShouldBeOfType<CelOuterFieldRef>().FieldName.ShouldBe("quantity");
        stock.Right.ShouldBeOfType<CelFieldRef>().FieldName.ShouldBe("stock");
    }

    [Theory]
    [InlineData("!product_id.discontinued")]
    [InlineData("product_id.discontinued == false")]
    [InlineData("!(product_id.discontinued && quantity > 1)")]
    public void A_negation_inside_a_check_is_allowed_because_it_stays_inside_the_exists(string expr)
    {
        var body = CompiledCheck(expr).Relation.Body;

        CelRewriter.Any(body, node => node is CelRelation or CelFieldRef { FieldName: "product_id" }).ShouldBeFalse();
        CelRewriter.Any(body, node => node is CelFieldRef { FieldName: "discontinued" }).ShouldBeTrue();
    }

    [Theory]
    [InlineData("product_id.owner_id == @user.id", "@user")]
    [InlineData("'manager' in @user.roles && product_id.active", "@user")]
    [InlineData("product_id.region_id == @tenant.id", "@tenant")]
    public void A_check_reading_a_caller_or_tenant_is_refused(string expr, string reads)
    {
        var error = SingleError(Validate(expr));

        error.Path.ShouldBe("/entities/order_lines/checks/0/expr");
        error.Message.ShouldBe($"A lookup check reads no caller: '{reads}' belongs in rules.");
        error.FixSuggestion.ShouldBe("Move the condition into the entity's rules.");
    }

    [Fact]
    public void A_check_that_reads_no_related_field_is_refused_with_the_validation_fix()
    {
        var error = SingleError(Validate("quantity > 0"));

        error.Path.ShouldBe("/entities/order_lines/checks/0/expr");
        error.Message.ShouldBe("A lookup check must read 'product_id': write the condition over 'product_id.<field>'.");
        error.FixSuggestion.ShouldBe("Use field 'validation' for a condition over this row alone.");
    }

    [Theory]
    [InlineData("supplier_id.active == true")]
    [InlineData("backup_id.active && product_id.active")]
    public void A_check_reading_another_ref_is_refused(string expr)
    {
        var error = SingleError(Validate(expr));

        error.Path.ShouldBe("/entities/order_lines/checks/0/expr");
        error.Message.ShouldStartWith("A lookup check follows 'product_id' only, so '");
    }

    [Fact]
    public void A_check_reaching_a_child_is_refused()
    {
        var error = SingleError(Validate("product_id.active && notes.exists(n, n.text == 'x')"));

        error.Path.ShouldBe("/entities/order_lines/checks/0/expr");
        error.Message.ShouldBe(
            "A lookup check reads the one row 'product_id' points at, so 'notes.exists(…)' has no rows to read.");
    }

    [Theory]
    [InlineData("quantity", "'quantity' is not a reference on 'order_lines', so a lookup check has no row to read.")]
    [InlineData("buyer_id", "'users' is the built-in users entity, so a rule cannot read its rows.")]
    [InlineData("missing", "'missing' is not a reference on 'order_lines', so a lookup check has no row to read.")]
    public void A_lookup_that_is_not_a_ref_or_points_at_users_is_refused(string lookup, string message)
    {
        var error = SingleError(_validator.Validate(Shop(lookup, $"{lookup}.active")));

        error.Path.ShouldBe("/entities/order_lines/checks/0/lookup");
        error.Message.ShouldBe(message);
    }

    [Fact]
    public void A_check_on_a_dynamic_entity_names_41()
    {
        var json = JsonNode.Parse(Shop("product_id", "product_id.active"))!.AsObject();
        json["entities"]!["order_lines"]!["storage"] = "dynamic";

        var error = SingleError(_validator.Validate(json.ToJsonString()));

        error.Path.ShouldBe("/entities/order_lines/checks/0");
        error.Message.ShouldBe("'order_lines' declares 'storage: dynamic', so its lookup checks have no table to read until #41.");
    }

    [Fact]
    public void The_reads_set_is_the_lookup_and_every_outer_field()
    {
        var check = CompiledCheck("has(note) && product_id.active && product_id.note != note && quantity > 0");

        check.Reads.Order(StringComparer.Ordinal).ShouldBe(["note", "product_id", "quantity"]);
        CelRewriter.Any(check.Relation.Body, node => node is CelOuterHas { Field.FieldName: "note" }).ShouldBeTrue();
        CelRewriter.Any(check.Relation.Body, node => node is CelFieldRef { FieldName: "note" }).ShouldBeTrue();
        CelRewriter.Any(check.Relation.Body, node => node is CelOuterFieldRef { FieldName: "note" }).ShouldBeTrue();
    }

    [Fact]
    public void An_outer_field_read_only_under_has_is_in_the_reads_set()
    {
        var check = CompiledCheck("has(memo) && product_id.active");

        check.Reads.Order(StringComparer.Ordinal).ShouldBe(["memo", "product_id"]);
    }

    [Fact]
    public void A_check_whose_target_list_is_three_deep_is_refused_at_its_own_path()
    {
        var json = Project(
            ("a", Entity([Ref("next_id", "b"), Field("flag", "boolean")], ("list", "next_id.flag == true"))),
            ("b", Entity([Ref("next_id", "c"), Field("flag", "boolean")], ("list", "next_id.flag == true"))),
            ("c", Entity([Ref("next_id", "d"), Field("flag", "boolean")], ("list", "next_id.flag == true"))),
            ("d", Entity([Field("flag", "boolean")], ("list", "true"))),
            ("x", WithCheck(Entity([Ref("a_id", "a")], ("list", "true")), "a_id", "a_id.flag")));

        var error = SingleError(_validator.Validate(json));

        error.Path.ShouldBe("/entities/x/checks/0/expr");
        error.Message.ShouldBe("'x.checks.0' reads relations 4 levels deep; at most 3 are allowed.");
    }

    [Fact]
    public void A_check_reading_an_entity_with_no_list_rule_is_a_warning()
    {
        var json = Project(
            ("products", Entity([Field("active", "boolean")])),
            ("order_lines", WithCheck(Entity([Ref("product_id", "products")], ("list", "true")), "product_id", "product_id.active")));

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeTrue();
        var warning = result.Errors.ShouldHaveSingleItem();
        warning.Severity.ShouldBe(DescriptorValidationSeverity.Warning);
        warning.Path.ShouldBe("/entities/order_lines/checks/0/expr");
        warning.Message.ShouldBe("'products' has no list rule, so this relation never matches any row.");
    }

    [Fact]
    public void A_list_rule_reading_back_through_a_check_is_no_cycle()
    {
        var json = Project(
            ("products", Entity([Field("active", "boolean")], ("list", "order_lines.exists(l, l.quantity > 0)"))),
            ("order_lines", WithCheck(
                Entity([Ref("product_id", "products"), Field("quantity", "integer")], ("list", "true")), "product_id", "product_id.active")));

        Errors(_validator.Validate(json)).ShouldBeEmpty();
    }

    [Fact]
    public void A_check_is_bound_through_the_engine_on_create_and_update_only()
    {
        var json = JsonNode.Parse(Shop("product_id", "product_id.active"))!.AsObject();
        json["entities"]!["order_lines"]!["rules"]!["update"] = "true";
        json["entities"]!["products"]!["rules"]!["list"] = "owner_id == @user.id";
        var descriptor = AlvoDescriptor.Parse(json.ToJsonString());
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent("checks", PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler));
        var engine = new PolicyEngine(provider);

        foreach (var operation in new[] { DataOperation.Create, DataOperation.Update })
        {
            var check = engine.Resolve("order_lines", operation, CelFixtures.Alice).Checks.ShouldHaveSingleItem();
            var scope = check.Relation.Scope.ShouldNotBeNull();
            scope.Denied.ShouldBeFalse();
            scope.Using.ShouldNotBeNull().Source.ShouldBe("owner_id == @user.id", "the check reads through the products list rule");
            check.Message.ShouldBe(Available);
        }

        var anonymous = new AlvoContext { User = default, Roles = new HashSet<Role> { Role.Anon } };
        engine.Resolve("order_lines", DataOperation.Create, anonymous).Checks.ShouldHaveSingleItem()
            .Relation.Scope.ShouldNotBeNull().Denied.ShouldBeTrue("a caller the products list rule cannot decide lists no product");

        engine.Resolve("order_lines", DataOperation.List, CelFixtures.Alice).Checks.ShouldBeEmpty();
        engine.Resolve("order_lines", DataOperation.Delete, CelFixtures.Alice).Checks.ShouldBeEmpty();
    }

    private static DescriptorCheck CompiledCheck(string expr)
    {
        var descriptor = AlvoDescriptor.Parse(Shop("product_id", expr));
        PolicyCatalog.TryBuild(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue(string.Join(" | ", errors.Select(error => $"{error.Path}: {error.Message}")));
        catalog!.TryGetEntity("order_lines", out var lines).ShouldBeTrue();
        return lines.Checks.ShouldHaveSingleItem();
    }

    private static DescriptorValidationResult Validate(string expr) => _validator.Validate(Shop("product_id", expr));

    private static DescriptorValidationError SingleError(DescriptorValidationResult result) =>
        Errors(result).ShouldHaveSingleItem(string.Join(" | ", result.Errors.Select(error => $"{error.Path}: {error.Message}")));

    private static List<DescriptorValidationError> Errors(DescriptorValidationResult result) =>
        [.. result.Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];

    /// <summary>
    /// <c>products</c> (listable) and <c>suppliers</c> as lookup targets; <c>order_lines</c> with one check over
    /// <paramref name="lookup"/>; <c>notes</c>, a child of <c>order_lines</c>.
    /// </summary>
    private static string Shop(string lookup, string expr)
    {
        var products = Entity(
            [Field("active", "boolean"), Field("discontinued", "boolean"), Field("stock", "integer"), Field("note", "string"),
             Field("owner_id", "uuid"), Field("region_id", "uuid")],
            ("list", "true"));
        var suppliers = Entity([Field("active", "boolean")], ("list", "true"));
        var lines = Entity(
            [Ref("product_id", "products"), Ref("backup_id", "products"), Ref("supplier_id", "suppliers"), Ref("buyer_id", "users"),
             Field("quantity", "integer"), Field("note", "string"), Field("memo", "string")],
            ("list", "true"), ("create", "true"));
        var notes = Entity([Ref("line_id", "order_lines"), Field("text", "string")], ("list", "true"));
        return Project(
            ("products", products), ("suppliers", suppliers), ("order_lines", WithCheck(lines, lookup, expr)), ("notes", notes));
    }

    private static JsonObject WithCheck(JsonObject entity, string lookup, string expr)
    {
        entity["checks"] = new JsonArray(new JsonObject { ["lookup"] = lookup, ["expr"] = expr, ["message"] = Available });
        return entity;
    }
}
