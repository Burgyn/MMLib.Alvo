using MMLib.Alvo.Analyzers.Generator;
using MMLib.Alvo.Data;
using System.Reflection;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>Names that are not C# identifiers still compile, stay stable, and keep their raw text in the attributes that map them.</summary>
public sealed class GeneratedNamesTests
{
    private const string Hostile = """
        { "apiVersion": "alvo.dev/v1", "name": "help-desk", "entities": { "tickets": { "fields": {
            "1st_call": { "type": "string" },
            "in-progress": { "type": "string" },
            "class": { "type": "string" },
            "ticket": { "type": "string" },
            "record": { "type": "string" },
            "to_string": { "type": "string" },
            "in_progress": { "type": "string" },
            "state": { "type": "enum", "values": ["in-progress", "In_Progress", "1st", "--"] } } } } }
        """;

    [Fact]
    public void Hostile_names_compile_and_keep_their_raw_text()
    {
        var assembly = GeneratorHarness.Load(Hostile);
        var ticket = assembly.GetType("HelpDesk.Ticket", throwOnError: true)!;
        var fields = ticket.GetProperties().Where(p => p.GetCustomAttribute<AlvoFieldAttribute>() is not null)
            .ToDictionary(p => p.Name, p => p.GetCustomAttribute<AlvoFieldAttribute>()!.Name);

        fields.ShouldBe(new Dictionary<string, string>
        {
            ["Id"] = "id",
            ["_1stCall"] = "1st_call",
            ["InProgress"] = "in-progress",
            ["Class"] = "class",
            ["TicketValue"] = "ticket",
            ["RecordValue"] = "record",
            ["ToStringValue"] = "to_string",
            ["InProgress2"] = "in_progress",
            ["State"] = "state",
        });
        var state = assembly.GetType("HelpDesk.TicketState", throwOnError: true)!;
        state.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (f.Name, f.GetCustomAttribute<AlvoEnumValueAttribute>()!.Value))
            .ShouldBe([("InProgress", "in-progress"), ("InProgress2", "In_Progress"), ("_1st", "1st"), ("Value3", "--")]);
    }

    private const string Reserved = """
        { "apiVersion": "alvo.dev/v1", "name": "help-desk", "auth": { "roles": ["to_string", "equals", "get_type", "reference_equals", "agent"] },
          "entities": { "tickets": { "fields": {
            "create": { "type": "string" },
            "patch": { "type": "string" },
            "equals": { "type": "string" },
            "get_hash_code": { "type": "string" },
            "get_type": { "type": "string" },
            "memberwise_clone": { "type": "string" },
            "finalize": { "type": "string" },
            "reference_equals": { "type": "string" },
            "kind": { "type": "enum", "values": ["to_string", "equals", "get_hash_code", "get_type", "has_flag", "compare_to", "get_type_code", "parse", "plain"] } } } } }
        """;

    [Fact]
    public void Names_an_inherited_or_generated_member_uses_are_suffixed_and_compile()
    {
        var assembly = GeneratorHarness.Load(Reserved);

        assembly.GetType("HelpDesk.Ticket", throwOnError: true)!.GetProperties()
            .Where(p => p.GetCustomAttribute<AlvoFieldAttribute>() is not null).Select(p => p.Name)
            .ShouldBe(["Id", "CreateValue", "PatchValue", "EqualsValue", "GetHashCodeValue", "GetTypeValue", "MemberwiseCloneValue", "FinalizeValue", "ReferenceEqualsValue", "Kind"]);
        assembly.GetType("HelpDesk.TicketKind", throwOnError: true)!.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name)
            .ShouldBe(["ToStringValue", "EqualsValue", "GetHashCodeValue", "GetTypeValue", "HasFlagValue", "CompareToValue", "GetTypeCodeValue", "ParseValue", "Plain"]);
        assembly.GetType("HelpDesk.HelpDeskRoles", throwOnError: true)!.GetFields().Select(f => f.Name)
            .ShouldBe(["Anon", "Authenticated", "Admin", "ToStringValue", "EqualsValue", "GetTypeValue", "ReferenceEqualsValue", "Agent"]);
    }

    [Fact]
    public void A_duplicated_field_key_keeps_its_last_definition_once()
    {
        var model = GeneratorHarness.Model(Descriptors.With(
            """{ "tickets": { "fields": { "title": { "type": "string" }, "body": { "type": "string" }, "title": { "type": "integer" } } } }"""));

        model.Entities[0].Fields.Select(f => (f.Name, f.Kind)).ShouldBe([("id", FieldKind.Uuid), ("body", FieldKind.String), ("title", FieldKind.Integer)]);
    }

    [Fact]
    public void A_keyword_parameter_is_escaped()
    {
        var model = GeneratorHarness.Model(Hostile);
        var source = EntityEmitter.Emit(model, model.Entities[0]);

        source.ShouldContain("string? @class = null");
        source.ShouldContain("[\"class\"] = @class,");
    }

    [Theory]
    [InlineData("tickets", "Ticket")]
    [InlineData("work_orders", "WorkOrder")]
    [InlineData("categories", "Category")]
    [InlineData("addresses", "Address")]
    [InlineData("classes", "Class")]
    [InlineData("boxes", "Box")]
    [InlineData("buzzes", "Buzz")]
    [InlineData("matches", "Match")]
    [InlineData("wishes", "Wish")]
    [InlineData("statuses", "Status")]
    [InlineData("status", "Status")]
    [InlineData("analysis", "Analysis")]
    [InlineData("news", "New")]
    [InlineData("data", "Data")]
    [InlineData("people", "Person")]
    [InlineData("children", "Child")]
    [InlineData("buses", "Bus")]
    [InlineData("order_statuses", "OrderStatus")]
    [InlineData("houses", "House")]
    [InlineData("cases", "Case")]
    [InlineData("responses", "Response")]
    [InlineData("branches", "Branch")]
    [InlineData("campus", "Campus")]
    public void Singular_rules(string entity, string className) => CSharpNames.Class(entity).ShouldBe(className);

    [Theory]
    [InlineData("records", "RecordEntity")]
    [InlineData("patches", "PatchEntity")]
    [InlineData("create", "CreateEntity")]
    public void A_class_name_the_class_s_own_members_use_is_suffixed(string entity, string className) =>
        CSharpNames.Class(entity).ShouldBe(className);

    [Theory]
    [InlineData("tasks", "TaskEntity")]
    [InlineData("types", "TypeEntity")]
    [InlineData("files", "FileEntity")]
    [InlineData("timers", "TimerEntity")]
    [InlineData("versions", "VersionEntity")]
    [InlineData("log_levels", "LogLevelEntity")]
    public void A_class_name_an_implicit_global_using_brings_in_is_suffixed(string entity, string className) =>
        CSharpNames.Class(entity).ShouldBe(className);

    [Fact]
    public void An_enum_name_an_implicit_global_using_brings_in_is_suffixed()
    {
        var model = GeneratorHarness.Model(Descriptors.With(
            """{ "values": { "fields": { "task": { "type": "enum", "values": ["a"] } } }, "tasks": { "fields": { "status": { "type": "enum", "values": ["open"] } } } }"""));

        model.Entities.Select(e => e.Fields.Single(f => f.EnumTypeName is not null).EnumTypeName).ShouldBe(["ValueTaskEnum", "TaskEntityStatus"]);
    }

    [Fact]
    public void No_patch_or_roles_name_can_clash_with_an_implicitly_imported_type() =>
        CSharpNames.ImplicitlyImportedTypeNames.Where(name => name.EndsWith("Patch", StringComparison.Ordinal) || name.EndsWith("Roles", StringComparison.Ordinal))
            .ShouldBeEmpty();

    [Fact]
    public void Every_emitted_type_name_is_listed_for_collision_checks()
    {
        var model = GeneratorHarness.Model(Descriptors.HelpDesk);

        CSharpNames.TypeNames(model.Entities[0]).ShouldBe(["Ticket", "TicketPatch", "TicketPriority", "TicketStatus"]);
        RolesEmitter.ClassName(model).ShouldBe("HelpDeskRoles");
    }

    [Theory]
    [InlineData("estimate_hours", "EstimateHours", "estimateHours")]
    [InlineData("class", "Class", "@class")]
    [InlineData("1st_call", "_1stCall", "_1stCall")]
    [InlineData("tenant-id", "TenantId", "tenantId")]
    public void Pascal_and_camel(string raw, string pascal, string camel)
    {
        CSharpNames.Pascal(raw).ShouldBe(pascal);
        CSharpNames.Camel(raw).ShouldBe(camel);
    }
}
