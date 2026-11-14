using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Compiles the descriptor's <c>automation</c> block at apply, in the policy catalog's own pass: each rule's trigger — an entity
/// event or a five-field UTC cron — its condition at the trigger's point, and every action through <see cref="ActionCompiler"/>,
/// so a rule's action and an after-hook's action are judged by one set of rules.
/// </summary>
/// <remarks>
/// What this build does not run is refused by name rather than accepted and ignored: batch delivery and the <c>.batch</c> event
/// shape (<see cref="UnhonouredFeatures.BatchDelivery"/>), an <c>auth</c> or <c>storage</c> trigger
/// (<see cref="UnhonouredFeatures.NonEntityTrigger"/>), and a condition on a cron rule, which has no row to read. A disabled rule
/// is checked like any other and left out of the catalog.
/// </remarks>
internal static class AutomationCompiler
{
    private const string EntitySource = "entity";
    private const string BatchSuffix = "batch";
    private const string CronZone = "UTC";

    /// <summary>
    /// The instant a cron is parsed against at apply: fixed, so compile reads no wall clock. A five-field cron repeats every
    /// year, so one that fires at all fires after any instant (HX-15 review m7).
    /// </summary>
    private static readonly DateTimeOffset _parseInstant = DateTimeOffset.UnixEpoch;

    private static readonly string[] _nonEntitySources = ["auth", "storage"];

    private static readonly Dictionary<string, (HandlerVerb Verb, string Point)> _operations = new(StringComparer.Ordinal)
    {
        ["created"] = (HandlerVerb.Created, "afterCreate"),
        ["updated"] = (HandlerVerb.Updated, "afterUpdate"),
        ["deleted"] = (HandlerVerb.Deleted, "afterDelete"),
    };

    /// <summary>Compiles every rule of <paramref name="scope"/>'s descriptor, appending every problem to its errors.</summary>
    /// <returns>The enabled rules that compiled, in declaration order.</returns>
    internal static IReadOnlyList<CompiledAutomationRule> Compile(AutomationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var rules = scope.Descriptor.Automation;
        if (rules is null || rules.Count == 0)
        {
            return [];
        }

        var twins = Twins(rules.Keys, scope.Errors);
        var compiled = new List<CompiledAutomationRule>();
        foreach (var (name, rule) in rules)
        {
            if (!twins.Contains(name) && CompileRule(name, rule, scope) is { } done && rule.Enabled != false)
            {
                compiled.Add(done);
            }
        }

        return compiled;
    }

    private static HashSet<string> Twins(IEnumerable<string> names, List<DescriptorValidationError> errors)
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        var twins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (first.TryAdd(ExtensionIds.DerivedSegment(name), name))
            {
                continue;
            }

            twins.Add(name);
            errors.Add(Error($"/automation/{name}", TwinMessage(name, first[ExtensionIds.DerivedSegment(name)]), "Rename one of the two rules."));
        }

        return twins;
    }

    private static string TwinMessage(string name, string other) =>
        $"Rule '{name}' differs from '{other}' only by a '-' and a '_', and both would be the event 'alvo.automation.{ExtensionIds.DerivedSegment(name)}'.";

    private static CompiledAutomationRule? CompileRule(string name, AutomationRule rule, AutomationScope scope)
    {
        var path = $"/automation/{name}";
        var errors = scope.Errors.Count;
        if (rule.Delivery == DeliveryMode.Batch)
        {
            scope.Errors.Add(Refused($"{path}/delivery", UnhonouredFeatures.BatchDelivery));
        }

        var compiled = rule.Trigger.Schedule is { } cron
            ? CompileCronRule(name, rule, cron, path, scope)
            : CompileEventRule(name, rule, rule.Trigger.Event ?? string.Empty, path, scope);

        return scope.Errors.Count == errors ? compiled : null;
    }

    private static CompiledAutomationRule? CompileEventRule(string name, AutomationRule rule, string pattern, string path, AutomationScope scope)
    {
        if (EventTriggerOf(pattern, $"{path}/trigger/event", scope) is not { } trigger)
        {
            return null;
        }

        var entity = scope.Schema.Entities.First(candidate => string.Equals(candidate.Name, trigger.Entity, StringComparison.Ordinal));
        var actionScope = ActionScope(entity, trigger.Point, $"/entities/{entity.Name}", scope);
        var condition = AfterHookCompiler.CompileCondition(rule.Condition, $"{path}/condition", actionScope);
        var actions = CompileActions(name, rule, path, actionScope);

        return new CompiledAutomationRule(
            name, trigger, condition, EventConditionReads.Required(condition), EventConditionReads.ReadsRoles(condition), actions);
    }

    private static EventTrigger? EventTriggerOf(string pattern, string path, AutomationScope scope)
    {
        var segments = pattern.Split('.');
        var refusal = EventPattern.HasWildcard(pattern) ? UnhonouredFeatures.WildcardSubscription
            : segments is [.., BatchSuffix] ? UnhonouredFeatures.BatchDelivery
            : _nonEntitySources.Contains(segments[0], StringComparer.Ordinal) ? UnhonouredFeatures.NonEntityTrigger
            : null;
        if (refusal is not null)
        {
            scope.Errors.Add(Refused(path, refusal));
            return null;
        }

        if (segments is [EntitySource, var entity, var operation])
        {
            return EntityTrigger(entity, operation, path, scope);
        }

        scope.Errors.Add(Error(path, $"'{pattern}' is not an entity event pattern.", "Write entity.<entity>.<created|updated|deleted>, such as 'entity.orders.created'."));
        return null;
    }

    private static EventTrigger? EntityTrigger(string entity, string operation, string path, AutomationScope scope)
    {
        var declared = scope.Schema.Entities.Select(candidate => candidate.Name).Order(StringComparer.Ordinal).ToList();
        if (!declared.Contains(entity, StringComparer.Ordinal))
        {
            scope.Errors.Add(Error(path, $"'{entity}' is not an entity of this descriptor, so this rule would never fire.", UnknownEntityFix(entity, declared)));
            return null;
        }

        if (_operations.TryGetValue(operation, out var known))
        {
            return new EventTrigger(entity, known.Verb, known.Point);
        }

        scope.Errors.Add(Error(path, $"'{operation}' is not an operation an entity emits, so this rule would never fire.", UnknownOperationFix(operation)));
        return null;
    }

    private static string UnknownEntityFix(string entity, List<string> declared) =>
        NameSuggestion.Closest(entity, declared) is { } closest
            ? $"Did you mean '{closest}'? Declared entities: {string.Join(", ", declared)}."
            : $"Declared entities: {string.Join(", ", declared)}.";

    private static string UnknownOperationFix(string operation) =>
        (NameSuggestion.Closest(operation, _operations.Keys) is { } closest ? $"Did you mean '{closest}'? " : string.Empty)
        + "An entity emits created, updated and deleted.";

    private static CompiledAutomationRule? CompileCronRule(string name, AutomationRule rule, string cron, string path, AutomationScope scope)
    {
        var tenanted = ScheduleChecks.IsScoped(scope.Schema);
        var trigger = CronTriggerOf(name, rule.Trigger, cron, tenanted, $"{path}/trigger/schedule", scope);
        if (rule.Condition is not null)
        {
            scope.Errors.Add(Error($"{path}/condition", CronCondition, CronConditionFix));
        }

        var actionScope = ActionScope(ScheduleRow(name, tenanted), CronPoint, path, scope);
        var actions = CompileActions(name, rule, path, actionScope);

        return trigger is null ? null : new CompiledAutomationRule(name, trigger, null, RequiredContext.None, false, actions);
    }

    private static CronTrigger? CronTriggerOf(string name, AutomationTrigger trigger, string cron, bool tenanted, string path, AutomationScope scope)
    {
        if (!CronSchedule.TryParse(cron, CronZone, _parseInstant, out _, out var problem))
        {
            scope.Errors.Add(Error(path, problem!, "Write five fields — minute hour day-of-month month day-of-week — read in UTC, such as '0 2 * * *'."));
            return null;
        }

        if (tenanted && !TenantCatalogs.Lists(scope.Host.Host, scope.Descriptor.Tenancy?.SelfService is not null))
        {
            scope.Errors.Add(NoTenantList(path, scope.Host.Host.HasTenantRegistry));
            return null;
        }

        return new CronTrigger(cron, DescriptorActionIds.Schedule(name, trigger), AutomationEvents.TypeFor(name), tenanted);
    }

    /// <summary>The row a cron rule's actions compile over: none — a schedule carries no record, only the event's own facts.</summary>
    private static EntitySchema ScheduleRow(string name, bool tenanted) => new()
    {
        Name = name,
        Fields = [],
        Tenancy = tenanted ? TenancyMode.Scoped : TenancyMode.Global,
    };

    private const string CronPoint = "afterCreate";

    private static List<CompiledRuleAction> CompileActions(string name, AutomationRule rule, string path, AfterHookScope scope)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var actions = new List<CompiledRuleAction>(rule.Actions.Count);
        for (var index = 0; index < rule.Actions.Count; index++)
        {
            var action = rule.Actions[index];
            if (ActionCompiler.Compile(action, $"{path}/actions/{index}", scope) is { } compiled)
            {
                var id = DescriptorActionIds.Unique(DescriptorActionIds.RuleAction(name, rule.Trigger, rule.Condition, action), taken);
                actions.Add(new CompiledRuleAction(id, compiled));
            }
        }

        return actions;
    }

    private static AfterHookScope ActionScope(EntitySchema row, string point, string entityPath, AutomationScope scope) =>
        new(row, scope.Compiler, scope.Templates, scope.Endpoints, entityPath, scope.Errors) { Point = point, Host = scope.Host };

    private static DescriptorValidationError Refused(string path, UnhonouredSlot slot) =>
        Error(path, $"'{slot.Feature}' is refused: {slot.Consequence}", slot.Fix);

    /// <summary>A cron rule with no tenant list; on a host with a registry the cure is <c>tenancy.selfService</c> too (S3 plan D13).</summary>
    private static DescriptorValidationError NoTenantList(string path, bool registry) => registry
        ? Error(path, $"{NoTenantListPrefix}{ScheduleChecks.NoTenantList(registry)}.", ScheduleChecks.ListTheTenants(registry) + NoTenantCatalogFixTail)
        : Error(path, NoTenantCatalog, NoTenantCatalogFix);

    private static DescriptorValidationError Error(string path, string message, string fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);

    private const string CronCondition =
        "A schedule rule has no row to judge, so a 'condition' on it could only ever read the clock — and this build does not evaluate one.";

    private const string CronConditionFix = "Remove 'condition', and decide inside the function the rule calls.";

    private const string NoTenantListPrefix = "A schedule rule on a tenanted project runs once per tenant, and ";

    private const string NoTenantCatalog = NoTenantListPrefix + "this host registers no IAlvoTenantCatalog.";

    private const string NoTenantCatalogFixTail = ", or trigger the rule from an entity event.";

    private const string NoTenantCatalogFix = "Register an IAlvoTenantCatalog" + NoTenantCatalogFixTail;
}

/// <summary>The framework event type a cron rule's schedule mints: <c>alvo.automation.&lt;rule&gt;</c>, <c>-</c> read as <c>_</c>.</summary>
internal static class AutomationEvents
{
    internal static string TypeFor(string rule) => "alvo.automation." + ExtensionIds.DerivedSegment(rule);
}

/// <summary>Everything the <c>automation</c> block compiles against, and the error accumulator it shares with the catalog.</summary>
internal sealed record AutomationScope(
    AlvoDescriptor Descriptor,
    SchemaModel Schema,
    ICelCompiler Compiler,
    IReadOnlyDictionary<string, MessageTemplate> Templates,
    IReadOnlyDictionary<string, WebhookEndpoint> Endpoints,
    ActionHost Host,
    List<DescriptorValidationError> Errors);

/// <summary>One enabled automation rule, compiled.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Trigger">What fires it.</param>
/// <param name="Condition">The condition at the trigger's point; <see langword="null"/> when it declares none or is a cron rule.</param>
/// <param name="Required">Which caller values <paramref name="Condition"/> reads that an envelope may lack.</param>
/// <param name="ReadsRoles">Whether <paramref name="Condition"/> reads <c>@user.roles</c>.</param>
/// <param name="Actions">The actions, each with its subscriber id, in declaration order.</param>
internal sealed record CompiledAutomationRule(
    string Name, CompiledTrigger Trigger, CompiledExpression? Condition, RequiredContext Required, bool ReadsRoles,
    IReadOnlyList<CompiledRuleAction> Actions);

/// <summary>What fires an automation rule.</summary>
internal abstract record CompiledTrigger;

/// <summary>One verb of one entity's data events.</summary>
/// <param name="Entity">The entity.</param>
/// <param name="Verb">The operation.</param>
/// <param name="Point">The after-point whose row images the condition and actions read: <c>afterCreate</c>, <c>afterUpdate</c> or <c>afterDelete</c>.</param>
internal sealed record EventTrigger(string Entity, HandlerVerb Verb, string Point) : CompiledTrigger;

/// <summary>A five-field UTC cron, run as a trigger-only schedule.</summary>
/// <param name="Cron">The cron, read in UTC.</param>
/// <param name="SubscriberId">The schedule's id.</param>
/// <param name="EventType">The framework event each occurrence mints.</param>
/// <param name="PerTenant">Whether each occurrence runs once per tenant — the project has a tenant-scoped entity.</param>
internal sealed record CronTrigger(string Cron, string SubscriberId, string EventType, bool PerTenant) : CompiledTrigger;

/// <summary>One compiled action of a rule, and the subscriber id its deliveries are recorded under.</summary>
internal sealed record CompiledRuleAction(string SubscriberId, CompiledAction Action);
