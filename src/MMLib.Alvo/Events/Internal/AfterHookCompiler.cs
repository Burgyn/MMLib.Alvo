using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Compiles one entity's <c>after*</c> hooks: the condition through <see cref="CelProfile.Condition"/>, every
/// <c>{{…}}</c> template parsed and validated against the entity's schema, and every reference to a webhook
/// endpoint or a message template resolved — all at <b>apply</b> time, into the
/// <see cref="Rules.PolicyCatalog"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It runs inside the policy catalog's own pass, and that is the design.</b> <see cref="EntitySchema"/> and
/// <see cref="SchemaModel"/> carry no hooks, so a hook catalog had two possible homes: the policy catalog's
/// existing priming or a fourth, independently primed holder. The second is the failure
/// <see cref="IPolicyCatalogProvider"/>'s remarks were written to prevent — it would mean a hook compiled
/// against a different schema revision than the rules judging the same write. So this type has exactly one
/// caller, <c>PolicyCatalogBuilder</c>, and shares its error accumulator: one pass, one priming site, one set
/// of errors.
/// </para>
/// <para>
/// <b>Everything is resolved here so that nothing is resolved at delivery.</b> At delivery there is nobody to
/// report a refusal to — a webhook that cannot render its body is a delivery that fails forever, retried
/// until it hits the attempt ceiling, and an author reading that sees an endpoint problem rather than a typo.
/// So a placeholder naming an undeclared field, an endpoint no <c>webhooks.endpoints</c> entry declares and a
/// template name no <c>templates</c> entry declares are all refused when the descriptor is applied.
/// </para>
/// <para>
/// <b>A refusal points at the slot an author edits.</b> An action's own slots hang off the hook
/// (<c>/entities/deals/hooks/afterUpdate/0/action/to</c>); a message template's <c>subject</c>/<c>body</c>
/// point at the template (<c>/templates/deal-won/body</c>), because that is the file position of the mistake
/// and because one template can be referenced from several entities — in which case it is validated once per
/// referencing entity, against that entity's schema.
/// </para>
/// </remarks>
internal static class AfterHookCompiler
{
    /// <summary>Compiles the three <c>after*</c> lists an entity declares, appending every problem to the scope.</summary>
    /// <param name="hooks">The entity's declared hooks, or <see langword="null"/> when it declares none.</param>
    /// <param name="scope">The schema, compiler, project-level references, pointer prefix and error accumulator.</param>
    /// <returns>
    /// The compiled catalog, or <see cref="EntityAfterHooks.None"/> when the entity declares no after-hook at
    /// all. A hook that failed to compile is absent from the result <em>and</em> present in the errors, so a
    /// catalog is never built holding a half-compiled action.
    /// </returns>
    internal static EntityAfterHooks Compile(EntityHooks? hooks, AfterHookScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (hooks is null)
        {
            return EntityAfterHooks.None;
        }

        var afterCreate = CompilePoint(AfterCreatePoint, hooks.AfterCreate, scope);
        var afterUpdate = CompilePoint(AfterUpdatePoint, hooks.AfterUpdate, scope);
        var afterDelete = CompilePoint(AfterDeletePoint, hooks.AfterDelete, scope);

        return afterCreate.Count + afterUpdate.Count + afterDelete.Count == 0
            ? EntityAfterHooks.None
            : new EntityAfterHooks(afterCreate, afterUpdate, afterDelete);
    }

    /// <summary>
    /// One action's <c>type</c> discriminator, spelled exactly as the frozen <c>$defs/action</c> does.
    /// </summary>
    /// <remarks>
    /// The one mapping, so a refusal cannot name an action by a spelling no descriptor can carry.
    /// <c>UnhonouredJsonataTests.Every_action_type_the_frozen_schema_declares_is_named</c> ties every arm to
    /// <c>schema/project.schema.json</c> itself, which is what makes the set right rather than merely unchanged.
    /// </remarks>
    /// <param name="action">The parsed action.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is a shape this mapping was never taught.</exception>
    internal static string ActionTypeName(AutomationAction action) => action switch
    {
        WebhookAction => WebhookType,
        EmailAction => EmailType,
        FunctionAction => FunctionType,
        EntityUpdateAction => EntityUpdateType,
        HttpCallAction => HttpCallType,
        _ => throw new ArgumentOutOfRangeException(
            nameof(action), action, "Unmapped action shape; name its 'type' discriminator here."),
    };

    private const string AfterCreatePoint = "afterCreate";
    private const string AfterUpdatePoint = "afterUpdate";
    private const string AfterDeletePoint = "afterDelete";

    private const string WebhookType = "webhook";
    private const string EmailType = "email";
    private const string FunctionType = "function";
    private const string EntityUpdateType = "entity.update";
    private const string HttpCallType = "http.call";

    private const string ToSlot = "to";
    private const string DataSlot = "data";
    private const string SubjectSlot = "subject";
    private const string BodySlot = "body";
    private const string BodyFileSlot = "bodyFile";
    private const string PayloadSlot = "payload";
    private const string EndpointSlot = "endpoint";
    private const string TemplateSlot = "template";
    private const string TypeSlot = "type";

    private static List<CompiledAfterHook> CompilePoint(
        string point, IReadOnlyList<AfterHook>? declared, AfterHookScope scope)
    {
        if (declared is null || declared.Count == 0)
        {
            return [];
        }

        var compiled = new List<CompiledAfterHook>(declared.Count);
        for (var index = 0; index < declared.Count; index++)
        {
            var hook = CompileHook(declared[index], $"{scope.EntityPath}/hooks/{point}/{index}", scope);
            if (hook is not null)
            {
                compiled.Add(hook);
            }
        }

        return compiled;
    }

    private static CompiledAfterHook? CompileHook(AfterHook hook, string path, AfterHookScope scope)
    {
        var condition = CompileCondition(hook.Condition, $"{path}/condition", scope);
        if (hook.Condition is not null && condition is null)
        {
            return null;
        }

        var action = CompileAction(hook.Action, $"{path}/action", scope);

        return action is null ? null : new CompiledAfterHook(path, condition, action);
    }

    /// <summary>
    /// Compiles a hook condition in the <see cref="CelProfile.Condition"/> profile — the only profile where
    /// <c>old.</c>, <c>new.</c> and <c>changed(field)</c> are legal, which is what an after-hook condition is
    /// written in.
    /// </summary>
    private static CompiledExpression? CompileCondition(string? source, string path, AfterHookScope scope)
    {
        if (source is null)
        {
            return null;
        }

        var result = scope.Compiler.Compile(source, CelProfile.Condition, scope.Schema);
        if (result.IsSuccess)
        {
            return result.Expression;
        }

        scope.Errors.AddRange(result.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
        return null;
    }

    private static CompiledAction? CompileAction(AutomationAction action, string path, AfterHookScope scope) =>
        action switch
        {
            WebhookAction webhook => CompileWebhook(webhook, path, scope),
            EmailAction email => CompileEmail(email, path, scope),
            _ => RefuseAction(action, path, scope),
        };

    private static CompiledAction? RefuseAction(AutomationAction action, string path, AfterHookScope scope)
    {
        var refusal = UnhonouredFeatures.UnhonouredAction(ActionTypeName(action));
        scope.Errors.Add(Error($"{path}/{TypeSlot}", refusal.Consequence, refusal.Fix));

        return null;
    }

    private static CompiledAction? CompileWebhook(WebhookAction webhook, string path, AfterHookScope scope)
    {
        if (!scope.Endpoints.ContainsKey(webhook.Endpoint))
        {
            scope.Errors.Add(UndeclaredReference(
                $"{path}/{EndpointSlot}", webhook.Endpoint, "webhooks.endpoints", scope.Endpoints.Keys));
            return null;
        }

        var templates = new Dictionary<string, AlvoTemplate>(StringComparer.Ordinal);

        return AddTransformSlot(templates, PayloadSlot, webhook.Payload, $"{path}/{PayloadSlot}", scope)
            ? new CompiledAction(webhook, templates)
            : null;
    }

    private static CompiledAction? CompileEmail(EmailAction email, string path, AfterHookScope scope)
    {
        if (!scope.Templates.TryGetValue(email.Template, out var message))
        {
            scope.Errors.Add(UndeclaredReference(
                $"{path}/{TemplateSlot}", email.Template, "templates", scope.Templates.Keys));
            return null;
        }

        var templates = new Dictionary<string, AlvoTemplate>(StringComparer.Ordinal);
        var recipient = AddSugarSlot(templates, ToSlot, email.To, $"{path}/{ToSlot}", scope);
        var data = AddTransformSlot(templates, DataSlot, email.Data, $"{path}/{DataSlot}", scope);
        var body = AddMessageTemplate(templates, email.Template, message, scope);

        return recipient && data && body ? new CompiledAction(email, templates) : null;
    }

    /// <summary>
    /// Adds the referenced message template's <c>subject</c> and <c>body</c>, refusing a <c>bodyFile</c>
    /// because nothing in this build reads a path inside a descriptor bundle.
    /// </summary>
    private static bool AddMessageTemplate(
        Dictionary<string, AlvoTemplate> templates, string name, MessageTemplate message, AfterHookScope scope)
    {
        var path = $"/templates/{name}";
        if (message.BodyFile is not null)
        {
            var refusal = UnhonouredFeatures.TemplateBodyFile;
            scope.Errors.Add(Error($"{path}/{BodyFileSlot}", refusal.Consequence, refusal.Fix));
            return false;
        }

        var subject = AddSugarSlot(templates, SubjectSlot, message.Subject, $"{path}/{SubjectSlot}", scope);

        return AddSugarSlot(templates, BodySlot, message.Body, $"{path}/{BodySlot}", scope) && subject;
    }

    /// <summary>
    /// A plain-string slot with <c>{{…}}</c> sugar — <c>email.to</c>, <c>templates.subject</c>/<c>body</c>.
    /// </summary>
    /// <remarks>
    /// The classifier is deliberately <em>not</em> consulted: the schema types these as plain strings, so a
    /// placeholder-free value is a legitimate literal (a hard-coded address) rather than evidence of JSONata.
    /// </remarks>
    private static bool AddSugarSlot(
        Dictionary<string, AlvoTemplate> templates, string slot, string? source, string path, AfterHookScope scope) =>
        source is null || AddTemplate(templates, slot, source, path, scope);

    /// <summary>
    /// A <c>$defs/jsonata</c>-typed slot — <c>webhook.payload</c>, <c>email.data</c>. Here a placeholder-free
    /// or brace-carrying string is raw JSONata and is refused by name; only a template goes through.
    /// </summary>
    private static bool AddTransformSlot(
        Dictionary<string, AlvoTemplate> templates, string slot, string? source, string path, AfterHookScope scope)
    {
        if (source is null)
        {
            return true;
        }

        if (JsonataSlot.IsTemplate(source))
        {
            return AddTemplate(templates, slot, source, path, scope);
        }

        var refusal = UnhonouredFeatures.RawJsonata;
        scope.Errors.Add(Error(path, refusal.Consequence, refusal.Fix));
        return false;
    }

    /// <summary>
    /// Parses one template and resolves every placeholder in it against the entity's schema, so the compiled
    /// action carries only templates that are known to render.
    /// </summary>
    private static bool AddTemplate(
        Dictionary<string, AlvoTemplate> templates, string slot, string source, string path, AfterHookScope scope)
    {
        if (!AlvoTemplate.TryParse(source, out var template, out var malformed))
        {
            scope.Errors.Add(Error(path, malformed!, MalformedTemplateFix));
            return false;
        }

        var refusals = Refusals(template!, scope.Schema);
        if (refusals.Count > 0)
        {
            scope.Errors.AddRange(refusals.Select(refusal => Error(path, refusal, UnresolvablePlaceholderFix)));
            return false;
        }

        templates[slot] = template!;
        return true;
    }

    private static IReadOnlyList<string> Refusals(AlvoTemplate template, EntitySchema entity) =>
        [.. template.Placeholders.Select(placeholder => Refusal(placeholder, entity)).OfType<string>()];

    private static string? Refusal(string placeholder, EntitySchema entity) =>
        TemplatePlaceholder.TryResolve(placeholder, entity, out var refusal) ? null : refusal;

    private const string MalformedTemplateFix =
        "Close every '{{' with a '}}' and put a single root-and-member placeholder inside each pair. It is "
        + "refused here rather than shipped as literal text, which is what an unclosed placeholder would "
        + "otherwise be delivered as.";

    private const string UnresolvablePlaceholderFix =
        "Fix the placeholder named in the message, or use a literal value. Templates are validated when the "
        + "descriptor is applied and never at delivery, where a refusal would be a delivery that fails until it "
        + "hits the attempt ceiling instead of an authoring error you can act on.";

    /// <summary>
    /// The "names something the project does not declare" refusal, reusing the same "did you mean" shape an
    /// unknown field, enum value or role literal gets — a typo is by far the likeliest cause.
    /// </summary>
    private static DescriptorValidationError UndeclaredReference(
        string path, string name, string block, IEnumerable<string> declared)
    {
        var candidates = declared.OrderBy(candidate => candidate, StringComparer.Ordinal).ToList();
        var closest = NameSuggestion.Closest(name, candidates);
        var known = candidates.Count == 0
            ? $"'{block}' declares none."
            : $"Declared under '{block}': {string.Join(", ", candidates)}.";

        return Error(
            path,
            $"'{name}' is not declared under '{block}', so this action could never run.",
            closest is not null ? $"Did you mean '{closest}'? {known}" : $"{known} Add it, or point at an existing one.");
    }

    private static DescriptorValidationError Error(string path, string message, string? fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}

/// <summary>
/// Everything one entity's after-hooks compile against: its schema, the CEL compiler, the project-level
/// blocks an action may reference, the entity's own JSON pointer, and the shared error accumulator.
/// </summary>
/// <remarks>
/// Bundled so every helper reads as "compile this slot at this path" rather than re-threading six arguments
/// that never vary within an entity — the same reason <c>PolicyCatalogBuilder</c> bundles its own.
/// </remarks>
/// <param name="Schema">The entity every condition and placeholder is checked against.</param>
/// <param name="Compiler">The CEL compiler every condition goes through.</param>
/// <param name="Templates">The descriptor's <c>templates</c>, or empty when it declares none.</param>
/// <param name="Endpoints">The descriptor's <c>webhooks.endpoints</c>, or empty when it declares none.</param>
/// <param name="EntityPath">The entity's JSON pointer, such as <c>/entities/deals</c>.</param>
/// <param name="Errors">The accumulator every problem is appended to — the policy catalog builder's own.</param>
internal sealed record AfterHookScope(
    EntitySchema Schema,
    ICelCompiler Compiler,
    IReadOnlyDictionary<string, MessageTemplate> Templates,
    IReadOnlyDictionary<string, WebhookEndpoint> Endpoints,
    string EntityPath,
    List<DescriptorValidationError> Errors);
