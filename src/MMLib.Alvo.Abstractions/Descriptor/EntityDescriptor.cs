using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// An entity definition (schema <c>$defs/entity</c>): its fields, storage, tenancy,
/// framework features, authorization rules, lifecycle hooks, and extra indexes.
/// </summary>
public sealed record EntityDescriptor
{
    /// <summary>Human-readable description of the entity.</summary>
    public string? Description { get; init; }

    /// <summary>Previous name of this entity, declaring a rename so apply preserves data.</summary>
    public string? RenamedFrom { get; init; }

    /// <summary>Physical layout of the entity; defaults to <see cref="StorageMode.Physical"/> when omitted.</summary>
    public StorageMode? Storage { get; init; }

    /// <summary>Tenancy scope of the entity; defaults per the top-level <see cref="Tenancy"/> block when omitted.</summary>
    public EntityTenancy? Tenancy { get; init; }

    /// <summary>Enables framework-managed soft delete (a managed <c>deleted_at</c> column).</summary>
    public bool? SoftDelete { get; init; }

    /// <summary>Injects the framework-managed audit columns (<c>created_at/by</c>, <c>updated_at/by</c>).</summary>
    public bool? Audit { get; init; }

    /// <summary>The entity's fields, keyed by field name.</summary>
    public required IReadOnlyDictionary<string, FieldDescriptor> Fields { get; init; }

    /// <summary>Per-operation authorization rules; a missing operation denies (secure-by-default).</summary>
    public AccessRules? Rules { get; init; }

    /// <summary>Lifecycle hooks (before* in-transaction, after* post-commit).</summary>
    public EntityHooks? Hooks { get; init; }

    /// <summary>Whether changes publish over the realtime channel; defaults to <see langword="true"/> when omitted.</summary>
    public bool? Realtime { get; init; }

    /// <summary>Explicit composite/extra indexes beyond the automatic ones.</summary>
    public IReadOnlyList<EntityIndex>? Indexes { get; init; }

    /// <summary>Extension keys (<c>x-*</c>) preserved verbatim through apply and export.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>An explicit composite/extra index on an entity (schema <c>entity.indexes[]</c>).</summary>
public sealed record EntityIndex
{
    /// <summary>Fields covered by the index, in order.</summary>
    public required IReadOnlyList<string> Fields { get; init; }

    /// <summary>Whether the index enforces uniqueness across the covered fields.</summary>
    public bool? Unique { get; init; }
}

/// <summary>
/// Per-operation authorization rules (schema <c>$defs/rules</c>): each is a CEL
/// condition compiled into a SQL predicate. A missing operation denies.
/// </summary>
public sealed record AccessRules
{
    /// <summary>CEL condition authorizing the list operation.</summary>
    public string? List { get; init; }

    /// <summary>CEL condition authorizing the get operation.</summary>
    public string? Get { get; init; }

    /// <summary>CEL condition authorizing the create operation.</summary>
    public string? Create { get; init; }

    /// <summary>CEL condition authorizing the update operation.</summary>
    public string? Update { get; init; }

    /// <summary>CEL condition authorizing the delete operation.</summary>
    public string? Delete { get; init; }
}

/// <summary>Lifecycle hooks grouped by trigger point (schema <c>entity.hooks</c>).</summary>
public sealed record EntityHooks
{
    /// <summary>Before-create hooks (in-transaction; reject or mutate).</summary>
    public IReadOnlyList<BeforeHook>? BeforeCreate { get; init; }

    /// <summary>Before-update hooks (in-transaction; reject or mutate).</summary>
    public IReadOnlyList<BeforeHook>? BeforeUpdate { get; init; }

    /// <summary>Before-delete hooks (in-transaction; reject or mutate).</summary>
    public IReadOnlyList<BeforeHook>? BeforeDelete { get; init; }

    /// <summary>After-create hooks (post-commit, from the outbox).</summary>
    public IReadOnlyList<AfterHook>? AfterCreate { get; init; }

    /// <summary>After-update hooks (post-commit, from the outbox).</summary>
    public IReadOnlyList<AfterHook>? AfterUpdate { get; init; }

    /// <summary>After-delete hooks (post-commit, from the outbox).</summary>
    public IReadOnlyList<AfterHook>? AfterDelete { get; init; }
}

/// <summary>A before-hook: an optional CEL condition and a reject/mutate action (schema <c>$defs/beforeHookList</c> item).</summary>
public sealed record BeforeHook
{
    /// <summary>Optional CEL condition gating the action.</summary>
    public string? Condition { get; init; }

    /// <summary>The in-transaction action to run: reject or mutate.</summary>
    public required BeforeHookAction Action { get; init; }
}

/// <summary>
/// A before-hook action: cancel the operation (<see cref="Reject"/>) or patch the
/// payload (<see cref="Mutate"/>). Exactly one is set.
/// </summary>
public sealed record BeforeHookAction
{
    /// <summary>When set, cancels the operation; the text becomes the RFC 7807 error detail.</summary>
    public string? Reject { get; init; }

    /// <summary>When set, patches the payload before write: field to literal value or tagged CEL expression.</summary>
    public IReadOnlyDictionary<string, ValueOrExpr>? Mutate { get; init; }
}

/// <summary>An after-hook: an optional CEL condition and a post-commit action (schema <c>$defs/afterHookList</c> item).</summary>
public sealed record AfterHook
{
    /// <summary>Optional CEL condition gating the action.</summary>
    public string? Condition { get; init; }

    /// <summary>The post-commit action to run.</summary>
    public required AutomationAction Action { get; init; }
}
