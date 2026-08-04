using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
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

    private const string AfterCreatePoint = "afterCreate";
    private const string AfterUpdatePoint = "afterUpdate";
    private const string AfterDeletePoint = "afterDelete";

    private const string BodyFileSlot = "bodyFile";
    private const string ConditionSlot = "condition";
    private const string EndpointSlot = "endpoint";
    private const string EndpointsBlock = "endpoints";
    private const string TemplateSlot = "template";
    private const string TypeSlot = "type";
    private const string UrlSlot = "url";

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
        var condition = CompileCondition(hook.Condition, $"{path}/{ConditionSlot}", scope);
        if (hook.Condition is not null && condition is null)
        {
            return null;
        }

        var action = CompileAction(hook.Action, $"{path}/action", scope);

        return action is null
            ? null
            : new CompiledAfterHook(path, condition, ActorRead(condition), action);
    }

    /// <summary>
    /// Compiles a hook condition in the <see cref="CelProfile.Condition"/> profile — the only profile where
    /// <c>old.</c>, <c>new.</c> and <c>changed(field)</c> are legal, which is what an after-hook condition is
    /// written in — and then refuses the two caller references the <em>envelope</em> cannot answer.
    /// </summary>
    private static CompiledExpression? CompileCondition(string? source, string path, AfterHookScope scope)
    {
        if (source is null)
        {
            return null;
        }

        var result = scope.Compiler.Compile(source, CelProfile.Condition, scope.Schema);
        if (!result.IsSuccess)
        {
            scope.Errors.AddRange(result.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
            return null;
        }

        return HonoursTheEnvelope(result.Expression!, path, scope) ? result.Expression : null;
    }

    /// <summary>
    /// Refuses <c>@tenant.id</c> and <c>@user.roles</c> in an after-hook condition, by name, exactly as
    /// <see cref="TemplatePlaceholder"/> refuses them in a template.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This closes an asymmetry rather than documenting one.</b> Both names compile in the
    /// <see cref="CelProfile.Condition"/> profile and both resolve against a post-commit context that cannot
    /// answer them, in opposite and equally silent directions. <c>@tenant.id</c> resolves to
    /// <see langword="null"/>, and the interpreter's null rule collapses <em>every</em> comparison — including
    /// <c>!=</c> — to <see langword="false"/>, so <c>changed(status) &amp;&amp; !(@tenant.id == 'internal')</c>
    /// reads as "every tenant except ours" and fires for <b>every</b> tenant, delivering the unmasked row to an
    /// external endpoint. <c>@user.roles</c> resolves to a non-null value and is worse for it: it answers with
    /// the <em>dispatcher's</em> role set, so <c>'admin' in @user.roles</c> is true for every event whoever
    /// wrote the row.
    /// </para>
    /// <para>
    /// <b>It is refused here and not in <see cref="CelTypeChecker"/>'s profile table</b>, because the reason is
    /// the envelope's and not the profile's: PR5b's before-hooks compile in the same profile and run inside the
    /// request, where both names have a real caller to resolve against. A profile-level ban would forbid them
    /// there too, for a reason that does not apply.
    /// </para>
    /// </remarks>
    private static bool HonoursTheEnvelope(CompiledExpression condition, string path, AfterHookScope scope)
    {
        var refusals = _unanswerable
            .Where(reference => PolicyCatalogBuilder.ReferencesContextValue(condition.Root, reference.Value))
            .Select(reference => Error(path, Unanswerable(reference), reference.Fix))
            .ToList();

        scope.Errors.AddRange(refusals);

        return refusals.Count == 0;
    }

    private static string Unanswerable(UnanswerableReference reference) =>
        $"This after-hook condition reads '{reference.Name}', which an after-hook cannot answer: "
        + $"{reference.Why}. The condition is evaluated after the write has committed, against the event "
        + "envelope, so a comparison against it is decided by the absence of the value rather than by the "
        + "value — silently, and not always in the denying direction.";

    /// <summary>
    /// The two caller references an event envelope cannot answer, with the words
    /// <see cref="EnvelopeProvenance"/> holds for both this refusal and the template one.
    /// </summary>
    private static readonly UnanswerableReference[] _unanswerable =
    [
        new(
            CelContextValue.TenantId,
            "@tenant.id",
            EnvelopeProvenance.NoTenant,
            EnvelopeProvenance.InsteadOfTenant($"new.{AlvoManagedColumns.TenantId}")),
        new(
            CelContextValue.UserRoles,
            "@user.roles",
            EnvelopeProvenance.NoRoles,
            EnvelopeProvenance.InsteadOfRoles),
    ];

    /// <summary>
    /// Which caller values the condition reads, so the dispatcher can refuse to select a hook whose condition
    /// needs an actor the envelope did not carry.
    /// </summary>
    /// <remarks>
    /// Only <c>@user.id</c> can survive <see cref="HonoursTheEnvelope"/>, so this is the same
    /// <see cref="RequiredContext"/> gate the policy engine applies to a rule — one shape for "this predicate
    /// reads an operand the caller may not have", rather than a second ad-hoc check.
    /// </remarks>
    private static RequiredContext ActorRead(CompiledExpression? condition) =>
        condition is null
            ? RequiredContext.None
            : new RequiredContext(
                TenantId: false,
                UserId: PolicyCatalogBuilder.ReferencesContextValue(condition.Root, CelContextValue.UserId));

    private static CompiledAction? CompileAction(AutomationAction action, string path, AfterHookScope scope) =>
        action switch
        {
            WebhookAction webhook => CompileWebhook(webhook, path, scope),
            EmailAction email => CompileEmail(email, path, scope),
            _ => RefuseAction(action, path, scope),
        };

    private static CompiledAction? RefuseAction(AutomationAction action, string path, AfterHookScope scope)
    {
        var refusal = UnhonouredFeatures.UnhonouredAction(ActionType.NameOf(action));
        scope.Errors.Add(Error($"{path}/{TypeSlot}", refusal.Consequence, refusal.Fix));

        return null;
    }

    private static CompiledAction? CompileWebhook(WebhookAction webhook, string path, AfterHookScope scope)
    {
        if (!scope.Endpoints.TryGetValue(webhook.Endpoint, out var endpoint))
        {
            scope.Errors.Add(UndeclaredReference(
                $"{path}/{EndpointSlot}", webhook.Endpoint, "webhooks.endpoints", scope.Endpoints.Keys));
            return null;
        }

        var target = ResolveTarget(webhook.Endpoint, endpoint, scope);
        var templates = new Dictionary<string, AlvoTemplate>(StringComparer.Ordinal);
        var payload = AddTransformSlot(
            templates, ActionSlot.Payload, webhook.Payload, $"{path}/{ActionSlot.Payload}", scope);

        return target is not null && payload ? new CompiledAction(webhook, templates, target, ActionType.NameOf(webhook)) : null;
    }

    /// <summary>
    /// Turns a declared endpoint into the <see cref="WebhookTarget"/> a delivery uses, refusing a URL that is
    /// not an absolute HTTPS one <b>here</b> rather than at delivery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The schema's <c>"format": "uri"</c> is an annotation, not an assertion</b> — nothing in the frozen
    /// schema rejects <c>/hooks/crm</c> or <c>htp://x</c>. Left to delivery, a malformed URL is a
    /// <see cref="UriFormatException"/> per attempt, retried to the ceiling and abandoned, which an author reads
    /// as an endpoint outage rather than as the typo it is. It is also the endpoint mistake an author is most
    /// likely to make, which is precisely the case apply-time resolution exists for.
    /// </para>
    /// <para>
    /// <b><c>http</c> is refused rather than warned, and the carve-out is loopback only.</b> The body is the
    /// unmasked record — <c>hidden</c> fields included — the delivery is unsigned, and the schema's own
    /// description of the slot says <em>HTTPS target</em>; cleartext is the one combination where decision D7's
    /// "bounded by who declares what" premise fails outright, because an on-path observer is nobody's author.
    /// A warning would describe a tolerance the apply path does not have, which is the argument
    /// <see cref="UnhonouredFeatures"/> already records for every one of its own entries. The exception is a
    /// <see cref="Uri.IsLoopback"/> host: there is no network to observe, and <c>http://localhost:5000/hook</c>
    /// is the shape a local receiver and this repository's own end-to-end suites use.
    /// </para>
    /// </remarks>
    private static WebhookTarget? ResolveTarget(string name, WebhookEndpoint endpoint, AfterHookScope scope)
    {
        var path = $"/webhooks/{EndpointsBlock}/{name}/{UrlSlot}";
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var url))
        {
            scope.Errors.Add(Error(path, NotAnAbsoluteUrl(endpoint.Url), AbsoluteUrlFix));
            return null;
        }

        return IsDeliverable(url) ? new WebhookTarget(name, url) : RefuseCleartext(path, url, scope);
    }

    private static bool IsDeliverable(Uri url) =>
        string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        || (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && url.IsLoopback);

    private static WebhookTarget? RefuseCleartext(string path, Uri url, AfterHookScope scope)
    {
        scope.Errors.Add(Error(path, NotHttps(url.Scheme), NotHttpsFix));

        return null;
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
        var recipient = AddSugarSlot(templates, ActionSlot.To, email.To, $"{path}/{ActionSlot.To}", scope);
        var data = RefuseEmailData(email.Data, $"{path}/{ActionSlot.Data}", scope);
        var body = AddMessageTemplate(templates, email.Template, message, scope);

        return recipient && data && body ? new CompiledAction(email, templates, Endpoint: null, ActionType.NameOf(email)) : null;
    }

    /// <summary>
    /// Refuses <c>email.data</c>, because nothing renders it: it was compiled, validated and stored under
    /// <see cref="ActionSlot.Data"/>, and <c>EventActionExecutor</c> reads only <c>to</c>, <c>subject</c> and
    /// <c>body</c>.
    /// </summary>
    /// <remarks>
    /// A slot that compiles cleanly and is then discarded is the exact failure mode a partial JSONata evaluator
    /// is refused for (<see cref="UnhonouredFeatures.RawJsonata"/>) — the action still runs and the body is not
    /// the one the author declared — except that the honoured fraction here is zero. A <c>data.*</c> placeholder
    /// root would be new surface and is PR5b's, so the slot is refused by name until something reads it.
    /// </remarks>
    private static bool RefuseEmailData(string? source, string path, AfterHookScope scope)
    {
        if (source is null)
        {
            return true;
        }

        var refusal = UnhonouredFeatures.EmailData;
        scope.Errors.Add(Error(path, refusal.Consequence, refusal.Fix));

        return false;
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

        var subject = AddSugarSlot(templates, ActionSlot.Subject, message.Subject, $"{path}/{ActionSlot.Subject}", scope);

        return AddSugarSlot(templates, ActionSlot.Body, message.Body, $"{path}/{ActionSlot.Body}", scope) && subject;
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

    private static string NotAnAbsoluteUrl(string url) =>
        $"'{url}' is not an absolute URL, so no delivery to this endpoint could ever be attempted. It is "
        + "refused when the descriptor is applied rather than at delivery, where it would be one failed attempt "
        + "per retry until the event hit the attempt ceiling and was abandoned.";

    private const string AbsoluteUrlFix =
        "Give the endpoint an absolute HTTPS URL, such as 'https://example.com/hooks/crm'. The schema's "
        + "\"format\": \"uri\" is an annotation and asserts nothing, so this is the only place the URL is checked.";

    private static string NotHttps(string scheme) =>
        $"This endpoint's URL uses '{scheme}', and a delivery carries the record's complete unmasked image — "
        + "'hidden' fields included — with no signature and no field projection. Over cleartext that image is "
        + "readable by anyone on the path, who is not the author the disclosure decision is bounded by.";

    private const string NotHttpsFix =
        "Use an 'https' URL. A cleartext 'http' URL is accepted only for a loopback host (localhost, 127.0.0.1, "
        + "[::1]), which is the local-receiver shape and has no network to observe.";

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

/// <summary>
/// One caller reference an after-hook condition may name and an event envelope cannot answer, and the words
/// its refusal is built from.
/// </summary>
/// <remarks>
/// A table rather than two <c>if</c> blocks for the reason <see cref="UnhonouredFeatures"/> is a table: the
/// wording, the detection and the fix belong to one entry, so a third unanswerable reference cannot be added
/// to the walk without the message that explains it.
/// </remarks>
/// <param name="Value">The compiled node kind the walk looks for.</param>
/// <param name="Name">How the reference is spelled in CEL, for the message an author reads.</param>
/// <param name="Why">Why the envelope cannot answer it — <see cref="EnvelopeProvenance"/>'s words.</param>
/// <param name="Fix">What to do instead, and where a real answer is tracked.</param>
internal sealed record UnanswerableReference(CelContextValue Value, string Name, string Why, string Fix);
