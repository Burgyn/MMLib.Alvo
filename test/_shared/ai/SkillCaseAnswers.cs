namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The right answers of the eval's proposing skill cases (spec §7.5), as JSON Patch against <c>bike-workshop</c> — one
/// source for the two suites that use them.
/// </summary>
/// <remarks>
/// <c>MMLib.Alvo.Host.Tests</c> dry-runs each through the real validator, so a case's right answer is one the framework
/// accepts; <c>MMLib.Alvo.Ai.Eval.Tests</c> applies each to its fixture to build the canned right turn its grader must
/// pass. Plain strings, so neither suite's types leak into the other.
/// </remarks>
internal static class SkillCaseAnswers
{
    /// <summary>The read rule of <c>own_orders_only</c>'s right answer.</summary>
    internal const string ReadRule = "'admin' in @user.roles || 'manager' in @user.roles || assigned_user_id == @user.id";

    /// <summary><c>function_action_refused</c>'s wrong answer: a <c>function</c> after-hook, which this build refuses.</summary>
    internal const string FunctionHook = """
        [{"op": "add", "path": "/entities/service_orders/hooks/afterUpdate/-",
          "value": {"condition": "new.status == 'ready'", "action": {"type": "function", "name": "invoice"}}}]
        """;

    private const string RejectPrice =
        """{"condition": "new.unit_price < 0.0", "action": {"reject": "The selling price of a part cannot be negative."}}""";

    /// <summary>
    /// <c>task_management_workers</c>' right answer (spec §9, D53): tasks linked to a technician, a customer and a part,
    /// and a discussion on each — audited, no managed column declared, and every owner clause on a ref to users.
    /// </summary>
    /// <remarks>
    /// Not in <see cref="RightAnswers"/>: that map's theories assume one skill per case, and this case needs three.
    /// </remarks>
    internal const string TaskManagement = """
        [{"op": "add", "path": "/entities/tasks",
          "value": {"audit": true,
                    "fields": {"title": {"type": "string", "required": true, "maxLength": 200},
                               "status": {"type": "enum", "required": true, "values": ["open", "done"], "default": "open"},
                               "technician_id": {"type": "ref", "entity": "technicians", "onDelete": "setNull"},
                               "assigned_user_id": {"type": "ref", "entity": "users"},
                               "customer_id": {"type": "ref", "entity": "customers", "onDelete": "setNull"},
                               "part_id": {"type": "ref", "entity": "parts", "onDelete": "setNull"}},
                    "rules": {"list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles",
                              "create": "'admin' in @user.roles || 'manager' in @user.roles",
                              "update": "'admin' in @user.roles || 'manager' in @user.roles || assigned_user_id == @user.id",
                              "delete": "'admin' in @user.roles"}}},
         {"op": "add", "path": "/entities/task_comments",
          "value": {"audit": true,
                    "fields": {"task_id": {"type": "ref", "entity": "tasks", "onDelete": "cascade", "required": true},
                               "author_id": {"type": "ref", "entity": "users", "required": true},
                               "body": {"type": "text", "required": true}},
                    "rules": {"list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles",
                              "create": "author_id == @user.id", "update": "author_id == @user.id",
                              "delete": "'admin' in @user.roles || author_id == @user.id"}}}]
        """;

    /// <summary>Each proposing case's right answer, by case name.</summary>
    internal static IReadOnlyDictionary<string, string> RightAnswers { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["hook_returned_at"] = """
            [{"op": "add", "path": "/entities/rentals/hooks/beforeUpdate",
              "value": [{"condition": "new.status == 'returned' && old.status != 'returned'",
                         "action": {"mutate": {"returned_at": {"$cel": "now()"}}}}]}]
            """,
        ["reject_negative_price"] = $$$"""
            [{"op": "add", "path": "/entities/parts/hooks", "value": {"beforeCreate": [{{{RejectPrice}}}], "beforeUpdate": [{{{RejectPrice}}}]}}]
            """,
        ["rollup_rentals_count"] = """
            [{"op": "add", "path": "/entities/customers/fields/rentals_count",
              "value": {"type": "integer", "rollup": {"from": "rentals", "op": "count"}}}]
            """,
        ["unique_part_per_order"] = """
            [{"op": "add", "path": "/entities/order_lines/indexes/-", "value": {"fields": ["part_id", "order_id"], "unique": true}}]
            """,
        ["own_orders_only"] = $$"""
            [{"op": "replace", "path": "/entities/service_orders/rules/list", "value": "{{ReadRule}}"},
             {"op": "replace", "path": "/entities/service_orders/rules/get", "value": "{{ReadRule}}"}]
            """,
    };
}
