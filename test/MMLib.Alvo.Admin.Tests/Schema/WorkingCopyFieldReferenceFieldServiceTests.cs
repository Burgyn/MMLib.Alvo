using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The places a field is named that arrived after <see cref="WorkingCopyFieldReferenceTests"/> (issue 513): a state
/// machine's field and its transitions' conditions, timers, lookup checks, an index's <c>where</c> and
/// <c>exclude</c>, a relation path in another entity's rules and checks, automation, an <c>entity.update</c>
/// payload and a sequence's <c>per</c>. Modelled on the field-service example.
/// </summary>
public class WorkingCopyFieldReferenceFieldServiceTests
{
    [Fact]
    public void A_rename_carries_a_transition_condition()
    {
        var copy = Copy();

        copy.RenameField("work_orders", "assigned_to", "owner_id", out var uncarried).ShouldBeNull();

        WorkOrders(copy)["stateMachine"]!["transitions"]!["schedule"]!["when"]!.GetValue<string>()
            .ShouldBe("has(new.owner_id) && has(new.respond_by)");
        uncarried.ShouldBe([new DescriptorReference("customers.rules.list", Blocks: false)]);
    }

    [Fact]
    public void A_rename_carries_a_relation_path_in_another_entitys_rule_and_leaves_its_own_same_named_field()
        => Renamed("work_orders", "assigned_to", "owner_id")["visits"]!["rules"]!["list"]!.GetValue<string>()
            .ShouldBe("work_order_id.owner_id == @user.id || assigned_to == @user.id");

    [Fact]
    public void A_rename_names_a_reverse_relation_it_cannot_rewrite_and_leaves_it_as_written()
    {
        var copy = Copy();

        copy.RenameField("work_orders", "assigned_to", "owner_id", out var uncarried).ShouldBeNull();

        uncarried.ShouldContain(new DescriptorReference("customers.rules.list", Blocks: false));
        Entities(copy)["customers"]!["rules"]!["list"]!.GetValue<string>()
            .ShouldBe("work_orders.exists(w, w.assigned_to == @user.id) || status == 'active'");
    }

    [Fact]
    public void A_rename_carries_the_state_machine_field_a_timer_condition_an_index_where_and_a_relation_path_in_a_check()
    {
        var entities = Renamed("work_orders", "status", "state");

        var orders = entities["work_orders"]!;
        orders["stateMachine"]!["field"]!.GetValue<string>().ShouldBe("state");
        orders["timers"]!["sla_breach"]!["when"]!.GetValue<string>().ShouldBe("is_emergency == true && state == 'new'");
        orders["indexes"]![0]!["where"]!.GetValue<string>().ShouldBe("state != 'done'");
        entities["visits"]!["checks"]![0]!["expr"]!.GetValue<string>().ShouldBe("work_order_id.state != 'done'");
        entities["visits"]!["rules"]!["update"]!.GetValue<string>().ShouldBe("technician_id == @user.id && work_order_id.state != 'done'");
    }

    [Fact]
    public void A_rename_carries_a_timers_at()
        => Renamed("work_orders", "respond_by", "respond_due")["work_orders"]!["timers"]!["sla_breach"]!["at"]!.GetValue<string>()
            .ShouldBe("respond_due");

    [Fact]
    public void A_rename_carries_an_index_exclusion_range()
    {
        var visits = Renamed("visits", "starts_at", "begins_at")["visits"]!;

        visits["indexes"]![0]!["exclude"]!.ToJsonString().ShouldBe("""{"from":"begins_at","to":"ends_at"}""");
    }

    [Fact]
    public void A_rename_carries_a_sequences_per()
        => Renamed("work_orders", "team", "crew")["work_orders"]!["fields"]!["job_number"]!["sequence"]!["per"]!.GetValue<string>()
            .ShouldBe("crew");

    [Fact]
    public void A_ref_rename_carries_its_relation_paths_and_the_checks_lookup()
    {
        var visits = Renamed("visits", "work_order_id", "job_id")["visits"]!;

        visits["rules"]!["list"]!.GetValue<string>().ShouldBe("job_id.assigned_to == @user.id || assigned_to == @user.id");
        visits["checks"]![0]!["lookup"]!.GetValue<string>().ShouldBe("job_id");
        visits["checks"]![0]!["expr"]!.GetValue<string>().ShouldBe("job_id.status != 'done'");
    }

    [Fact]
    public void A_rename_carries_the_condition_of_an_automation_its_entity_triggers_and_no_other()
    {
        var copy = Copy();

        copy.RenameField("work_orders", "po_number", "po_ref").ShouldBeNull();
        copy.RenameField("work_orders", "assigned_to", "owner_id").ShouldBeNull();

        var automation = Node(copy)["automation"]!;
        automation["completed"]!["condition"]!.GetValue<string>().ShouldBe("changed(status) && has(new.po_ref)");
        automation["completed"]!["actions"]![0]!["payload"]!["technician_id"]!["$cel"]!.GetValue<string>().ShouldBe("new.owner_id");
        automation["visit-booked"]!["condition"]!.GetValue<string>().ShouldBe("has(new.assigned_to)");
    }

    [Fact]
    public void A_rename_carries_the_payload_key_of_an_entity_update_that_writes_its_entity()
    {
        var copy = Copy();

        copy.RenameField("visits", "technician_id", "engineer_id", out var uncarried).ShouldBeNull();

        Node(copy)["automation"]!["completed"]!["actions"]![0]!["payload"]!.ToJsonString().ShouldBe("""{"engineer_id":{"$cel":"new.assigned_to"}}""");
        Entities(copy)["visits"]!["hooks"]!["afterCreate"]![0]!["action"]!["payload"]!.ToJsonString()
            .ShouldBe("""{"assigned_to":{"$cel":"new.engineer_id"}}""");
        uncarried.ShouldBeEmpty();
    }

    [Fact]
    public void A_rename_carries_the_payload_key_another_entitys_hook_writes()
        => Renamed("work_orders", "assigned_to", "owner_id")["visits"]!["hooks"]!["afterCreate"]![0]!["action"]!["payload"]!.ToJsonString()
            .ShouldBe("""{"owner_id":{"$cel":"new.technician_id"}}""");

    [Fact]
    public void A_removal_names_a_transition_a_relation_path_a_reverse_relation_and_the_actions_that_name_the_field()
    {
        var references = Copy().ReferencesToField("work_orders", "assigned_to");

        references.ShouldContain(new DescriptorReference("work_orders.stateMachine.transitions.schedule.when", Blocks: true));
        references.ShouldContain(new DescriptorReference("visits.rules.list", Blocks: true));
        references.ShouldContain(new DescriptorReference("customers.rules.list", Blocks: false));
        references.ShouldContain(new DescriptorReference("visits.hooks.afterCreate[0].action.payload", Blocks: true));
        references.ShouldContain(new DescriptorReference("automation.completed.actions[0].payload.technician_id", Blocks: true));
        references.ShouldNotContain(reference => reference.Place == "automation.visit-booked.condition");
    }

    [Fact]
    public void A_removal_names_the_state_machine_a_timer_an_index_where_an_automation_and_another_entitys_check()
    {
        var references = Copy().ReferencesToField("work_orders", "status");

        references.ShouldContain(new DescriptorReference("work_orders.stateMachine.field", Blocks: true));
        references.ShouldContain(new DescriptorReference("work_orders.timers.sla_breach.when", Blocks: true));
        references.ShouldContain(new DescriptorReference("work_orders.indexes[0].where", Blocks: true));
        references.ShouldContain(new DescriptorReference("automation.completed.condition", Blocks: true));
        references.ShouldContain(new DescriptorReference("visits.checks[0].expr", Blocks: true));
        references.ShouldContain(new DescriptorReference("visits.rules.update", Blocks: true));
    }

    [Fact]
    public void A_removal_names_a_timers_at()
        => Copy().ReferencesToField("work_orders", "respond_by")
            .ShouldContain(new DescriptorReference("work_orders.timers.sla_breach.at", Blocks: true));

    [Fact]
    public void A_removal_names_an_index_exclusion_range()
        => Copy().ReferencesToField("visits", "ends_at")
            .ShouldBe([new DescriptorReference("visits.indexes[0].exclude.to", Blocks: true)]);

    [Fact]
    public void A_removal_names_a_sequences_per()
        => Copy().ReferencesToField("work_orders", "team")
            .ShouldBe([new DescriptorReference("work_orders.fields.job_number.sequence.per", Blocks: true)]);

    /// <summary>
    /// <c>work_orders.exists(…)</c> reaches the child through its one ref to the parent (<c>RelationCatalogBuilder</c>), so
    /// removing that ref leaves the parent's rule nothing to reach through — the rollup's implicit <c>via</c>, for rules.
    /// </summary>
    [Fact]
    public void A_childs_only_ref_removal_blocks_on_the_parent_rule_that_reaches_the_child_through_it()
        => Copy().ReferencesToField("work_orders", "customer_id").ShouldBe(
        [
            new DescriptorReference("work_orders.indexes[0]", Blocks: true),
            new DescriptorReference("customers.rules.list", Blocks: true),
        ]);

    [Fact]
    public void A_via_receiver_blocks_the_removal_of_the_ref_it_names_and_is_named_by_its_rename()
    {
        var copy = Copy(Descriptor.Replace("\"work_orders.exists(", "\"work_orders_via_customer_id.exists(", StringComparison.Ordinal));

        copy.ReferencesToField("work_orders", "customer_id").ShouldContain(new DescriptorReference("customers.rules.list", Blocks: true));
        copy.RenameField("work_orders", "customer_id", "client_id", out var uncarried).ShouldBeNull();
        uncarried.ShouldBe([new DescriptorReference("customers.rules.list", Blocks: false)]);
    }

    /// <summary>The parent's binder reaches <c>work_orders</c>; its own <c>status</c> beside it is no reference to the child's.</summary>
    [Fact]
    public void A_parents_same_named_column_beside_a_binder_over_the_child_is_no_reference()
    {
        var copy = Copy();

        copy.ReferencesToField("work_orders", "status").ShouldNotContain(reference => reference.Place == "customers.rules.list");
        copy.RenameField("work_orders", "status", "state", out var uncarried).ShouldBeNull();
        uncarried.ShouldBeEmpty();
    }

    [Fact]
    public void Renaming_the_childs_same_named_field_leaves_the_path_through_the_ref_and_the_parents_payload_key()
    {
        var copy = Copy();

        copy.RenameField("visits", "assigned_to", "booked_by").ShouldBeNull();

        var visits = Entities(copy)["visits"]!;
        visits["rules"]!["list"]!.GetValue<string>().ShouldBe("work_order_id.assigned_to == @user.id || booked_by == @user.id");
        visits["hooks"]!["afterCreate"]![0]!["action"]!["payload"]!.ToJsonString().ShouldBe("""{"assigned_to":{"$cel":"new.technician_id"}}""");
        WorkOrders(copy)["stateMachine"]!["transitions"]!["schedule"]!["when"]!.GetValue<string>().ShouldBe("has(new.assigned_to) && has(new.respond_by)");
        Node(copy)["automation"]!["visit-booked"]!["condition"]!.GetValue<string>().ShouldBe("has(new.booked_by)");
    }

    [Fact]
    public void A_rename_through_one_ref_leaves_another_refs_member_a_string_literal_and_the_callers_claim()
        => Renamed("work_orders", "status", "state")["visits"]!["rules"]!["delete"]!.GetValue<string>()
            .ShouldBe("customer_id.status == 'gone' || work_order_id.state == 'done' || 'status' == @user.status");

    /// <summary>A self-ref read through itself: both the head and the member are the renamed ref.</summary>
    [Fact]
    public void A_self_ref_rename_carries_a_path_that_reads_it_through_itself()
    {
        var copy = Copy("""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "tree",
              "entities": {
                "nodes": {
                  "fields": { "parent_id": { "type": "ref", "entity": "nodes" }, "owner_id": { "type": "uuid" } },
                  "rules": { "list": "parent_id.parent_id == null || parent_id.owner_id == @user.id" }
                }
              }
            }
            """);

        copy.RenameField("nodes", "parent_id", "parent", out var uncarried).ShouldBeNull();

        Entities(copy)["nodes"]!["rules"]!["list"]!.GetValue<string>().ShouldBe("parent.parent == null || parent.owner_id == @user.id");
        uncarried.ShouldBeEmpty();
    }

    [Fact]
    public void A_refs_removal_names_the_checks_lookup_and_its_relation_paths()
    {
        var references = Copy().ReferencesToField("visits", "work_order_id");

        references.ShouldContain(new DescriptorReference("visits.checks[0].lookup", Blocks: true));
        references.ShouldContain(new DescriptorReference("visits.checks[0].expr", Blocks: true));
        references.ShouldContain(new DescriptorReference("visits.rules.list", Blocks: true));
    }

    [Fact]
    public void A_removal_names_the_entity_update_that_writes_it_and_the_hook_value_that_reads_it()
    {
        var references = Copy().ReferencesToField("visits", "technician_id");

        references.ShouldContain(new DescriptorReference("automation.completed.actions[0].payload", Blocks: true));
        references.ShouldContain(new DescriptorReference("visits.hooks.afterCreate[0].action.payload.assigned_to", Blocks: true));
    }

    [Fact]
    public void Asking_what_names_a_field_changes_nothing()
    {
        var copy = Copy();
        var before = copy.Json;

        copy.ReferencesToField("work_orders", "assigned_to");
        copy.ReferencesToField("work_orders", "status");
        copy.ReferencesToField("work_orders", "customer_id");
        copy.ReferencesToField("visits", "technician_id");

        copy.Json.ShouldBe(before);
    }

    private static WorkingCopy Copy(string descriptor = Descriptor)
    {
        var copy = new WorkingCopy();
        copy.Take(descriptor, revision: 3);
        return copy;
    }

    private static JsonNode Renamed(string entity, string from, string to)
    {
        var copy = Copy();
        copy.RenameField(entity, from, to).ShouldBeNull();
        return Entities(copy);
    }

    private static JsonNode Node(WorkingCopy copy) => JsonNode.Parse(copy.Json)!;

    private static JsonNode Entities(WorkingCopy copy) => Node(copy)["entities"]!;

    private static JsonNode WorkOrders(WorkingCopy copy) => Entities(copy)["work_orders"]!;

    private const string Descriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "field-service",
          "entities": {
            "customers": {
              "fields": { "name": { "type": "string" }, "status": { "type": "string" } },
              "rules": { "list": "work_orders.exists(w, w.assigned_to == @user.id) || status == 'active'" }
            },
            "work_orders": {
              "fields": {
                "status": { "type": "enum", "values": ["new", "scheduled", "done"], "default": "new" },
                "assigned_to": { "type": "uuid" },
                "respond_by": { "type": "datetime" },
                "is_emergency": { "type": "boolean" },
                "po_number": { "type": "string" },
                "customer_id": { "type": "ref", "entity": "customers" },
                "team": { "type": "string" },
                "job_number": { "type": "integer", "sequence": { "per": "team" } }
              },
              "rules": { "list": "assigned_to == @user.id" },
              "stateMachine": {
                "field": "status",
                "initial": "new",
                "transitions": { "schedule": { "from": ["new"], "to": "scheduled", "when": "has(new.assigned_to) && has(new.respond_by)" } }
              },
              "timers": { "sla_breach": { "at": "respond_by", "when": "is_emergency == true && status == 'new'" } },
              "indexes": [ { "fields": ["customer_id", "po_number"], "unique": true, "where": "status != 'done'" } ]
            },
            "visits": {
              "fields": {
                "work_order_id": { "type": "ref", "entity": "work_orders" },
                "customer_id": { "type": "ref", "entity": "customers" },
                "technician_id": { "type": "uuid" },
                "assigned_to": { "type": "uuid" },
                "starts_at": { "type": "datetime" },
                "ends_at": { "type": "datetime" }
              },
              "rules": {
                "list": "work_order_id.assigned_to == @user.id || assigned_to == @user.id",
                "update": "technician_id == @user.id && work_order_id.status != 'done'",
                "delete": "customer_id.status == 'gone' || work_order_id.status == 'done' || 'status' == @user.status"
              },
              "checks": [ { "lookup": "work_order_id", "expr": "work_order_id.status != 'done'", "message": "Open orders only." } ],
              "indexes": [ { "fields": ["technician_id"], "exclude": { "from": "starts_at", "to": "ends_at" } } ],
              "hooks": {
                "afterCreate": [
                  { "action": { "type": "entity.update", "entity": "work_orders", "recordId": "{{new.work_order_id}}", "payload": { "assigned_to": { "$cel": "new.technician_id" } } } }
                ]
              }
            }
          },
          "automation": {
            "completed": {
              "trigger": { "event": "entity.work_orders.updated" },
              "condition": "changed(status) && has(new.po_number)",
              "actions": [ { "type": "entity.update", "entity": "visits", "recordId": "{{new.id}}", "payload": { "technician_id": { "$cel": "new.assigned_to" } } } ]
            },
            "visit-booked": {
              "trigger": { "event": "entity.visits.created" },
              "condition": "has(new.assigned_to)",
              "actions": [ { "type": "webhook", "endpoint": "billing" } ]
            }
          }
        }
        """;
}
