using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The <c>type</c> discriminator of every action the frozen <c>$defs/action</c> declares, and the one mapping
/// from a parsed action to it.
/// </summary>
/// <remarks>
/// <para>
/// It lives beside <see cref="ActionSlot"/> rather than on the compiler, because it is the descriptor's
/// vocabulary and not the compiler's work: the apply-time refusals name an action with it, and the executor
/// writes the same name into the action log. A shared spelling is the whole point — a refusal that named an
/// action by a spelling no descriptor can carry is unactionable, and a log line that used a second spelling
/// would not join up with the refusal an author read.
/// </para>
/// <para>
/// <c>UnhonouredJsonataTests.Every_action_type_the_frozen_schema_declares_is_named</c> ties every arm to
/// <c>schema/project.schema.json</c> itself, which is what makes the set right rather than merely unchanged.
/// </para>
/// </remarks>
internal static class ActionType
{
    /// <summary><c>type: webhook</c> — honoured from an after-hook in this build.</summary>
    internal const string Webhook = "webhook";

    /// <summary><c>type: email</c> — honoured from an after-hook in this build.</summary>
    internal const string Email = "email";

    /// <summary><c>type: function</c> — declared by the schema, refused when a descriptor is applied.</summary>
    internal const string Function = "function";

    /// <summary><c>type: entity.update</c> — declared by the schema, refused when a descriptor is applied.</summary>
    internal const string EntityUpdate = "entity.update";

    /// <summary><c>type: http.call</c> — declared by the schema, refused when a descriptor is applied.</summary>
    internal const string HttpCall = "http.call";

    /// <summary>One action's <c>type</c> discriminator, spelled exactly as the frozen schema spells it.</summary>
    /// <param name="action">The parsed action.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is a shape this mapping was never taught.</exception>
    internal static string NameOf(AutomationAction action) => action switch
    {
        WebhookAction => Webhook,
        EmailAction => Email,
        FunctionAction => Function,
        EntityUpdateAction => EntityUpdate,
        HttpCallAction => HttpCall,
        _ => throw new ArgumentOutOfRangeException(
            nameof(action), action, "Unmapped action shape; name its 'type' discriminator here."),
    };
}

/// <summary>
/// The slot names an action's compiled templates are keyed by, spelled exactly as the frozen
/// <c>$defs/action</c> and <c>$defs/template</c> spell them.
/// </summary>
/// <remarks>
/// One authority, shared by the compiler that writes the dictionary and the executor that reads it. Two
/// spellings of a key would not fail to build and would not fail to apply: the slot would simply have no
/// entry, and the executor would render an empty recipient or post the canonical envelope where a payload was
/// declared — a wrong delivery that looks exactly like a successful one.
/// </remarks>
internal static class ActionSlot
{
    /// <summary><c>email.to</c> — the recipient, a plain-string sugar slot.</summary>
    internal const string To = "to";

    /// <summary><c>templates.subject</c> — the subject line, a plain-string sugar slot.</summary>
    internal const string Subject = "subject";

    /// <summary><c>templates.body</c> — the inline body, a plain-string sugar slot.</summary>
    internal const string Body = "body";

    /// <summary><c>webhook.payload</c> — the outbound payload, a <c>$defs/jsonata</c> slot.</summary>
    internal const string Payload = "payload";

    /// <summary><c>email.data</c> — the template's data, a <c>$defs/jsonata</c> slot.</summary>
    internal const string Data = "data";
}
