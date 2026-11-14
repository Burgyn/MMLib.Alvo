namespace MMLib.Alvo.Descriptor;

/// <summary>Identity of the project/backend shown wherever it is presented (schema <c>branding</c>).</summary>
public sealed record Branding
{
    /// <summary>Display name of this project/backend.</summary>
    public string? Title { get; init; }

    /// <summary>URL or path to this project's logo.</summary>
    public string? LogoUrl { get; init; }
}

/// <summary>Declares this backend as multi-tenant (schema <c>tenancy</c>).</summary>
public sealed record Tenancy
{
    /// <summary>Turn on row-level multi-tenancy (shared DB + shared schema, discriminator column).</summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Self-service tenants for signed-in people (schema <c>tenancy.selfService</c>); <see langword="null"/> keeps tenants
    /// to API keys and operator grants. Requires <see cref="Enabled"/>.
    /// </summary>
    public TenancySelfService? SelfService { get; init; }
}

/// <summary>Which tenants signed-in people get and manage themselves (schema <c>tenancy.selfService</c>).</summary>
public sealed record TenancySelfService
{
    /// <summary>Present: every person gets one tenant of their own at sign-up.</summary>
    public SelfServicePersonalTenant? PersonalTenant { get; init; }

    /// <summary>Present: a person may create shared tenants.</summary>
    public SelfServiceCreateTenants? CreateTenants { get; init; }

    /// <summary>Whether owners may invite people by link; requires <see cref="CreateTenants"/>.</summary>
    public bool? Invitations { get; init; }

    /// <summary>
    /// The tenant roles: the only roles a membership can ever carry, each declared in <c>auth.roles</c> and never a
    /// built-in role.
    /// </summary>
    public IReadOnlyList<string>? AssignableRoles { get; init; }
}

/// <summary>The tenant every person gets of their own (schema <c>tenancy.selfService.personalTenant</c>).</summary>
public sealed record SelfServicePersonalTenant
{
    /// <summary>The tenant roles a person holds in their personal tenant, each in <c>assignableRoles</c>; default none.</summary>
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>Whether and how a person may create shared tenants (schema <c>tenancy.selfService.createTenants</c>).</summary>
public sealed record SelfServiceCreateTenants
{
    /// <summary>The tenant roles the creator receives, each in <c>assignableRoles</c>.</summary>
    public IReadOnlyList<string>? CreatorRoles { get; init; }

    /// <summary>How many shared tenants one account may create: 1–100, default 5.</summary>
    public int? MaxPerAccount { get; init; }
}

/// <summary>
/// Governance for runtime, user-defined entities — the dynamic schema-registry
/// driver (schema <c>dynamicEntities</c>).
/// </summary>
public sealed record DynamicEntities
{
    /// <summary>Allow end-users to define entities at runtime.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Reserved name prefix for runtime-created entities, so they never collide with declared names.</summary>
    public string? NamePrefix { get; init; }

    /// <summary>Authorization rules applied to every runtime-created entity.</summary>
    public AccessRules? DefaultRules { get; init; }

    /// <summary>The subset of field types end-users may use on runtime entities.</summary>
    public IReadOnlyList<FieldType>? AllowedFieldTypes { get; init; }

    /// <summary>Per-entity field quota for runtime entities.</summary>
    public int? MaxFieldsPerEntity { get; init; }

    /// <summary>Per-entity record quota for runtime entities (per tenant).</summary>
    public int? MaxRecordsPerEntity { get; init; }

    /// <summary>How many runtime entities a single tenant may create.</summary>
    public int? MaxEntitiesPerTenant { get; init; }

    /// <summary>Default tenancy for runtime-created entities; defaults to <see cref="EntityTenancy.Scoped"/> when omitted.</summary>
    public EntityTenancy? DefaultTenancy { get; init; }
}

/// <summary>Authentication providers and application roles (schema <c>auth</c>).</summary>
public sealed record Auth
{
    /// <summary>Enabled identity providers.</summary>
    public IReadOnlyList<AuthProvider>? Providers { get; init; }

    /// <summary>Application roles beyond the built-in ones (anon, authenticated, admin).</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>
    /// Who may create an account by signing in; required once <see cref="Providers"/> lists a provider Alvo honours
    /// for end users (today <c>google</c>).
    /// </summary>
    public AuthSignUp? SignUp { get; init; }
}

/// <summary>Who may create an account by signing in with an end-user provider (schema <c>auth.signUp</c>).</summary>
public sealed record AuthSignUp
{
    /// <summary>Whether any person the provider vouches for may create an account, or only one accepting an invitation.</summary>
    public required SignUpMode Mode { get; init; }

    /// <summary>
    /// The domains a <b>trusted</b> email must be in for its owner to sign up, lower-case hostnames; <see langword="null"/>
    /// admits any domain.
    /// </summary>
    public IReadOnlyList<string>? AllowedEmailDomains { get; init; }
}

/// <summary>Who may manage this project in the admin dashboard, mapped to levels via CEL (schema <c>access</c>).</summary>
public sealed record Access
{
    /// <summary>CEL: who may fully administer this project.</summary>
    public string? Admin { get; init; }

    /// <summary>CEL: who may edit schema, rules, and automation, but not settings.</summary>
    public string? Developer { get; init; }

    /// <summary>CEL: who may view data and configuration read-only.</summary>
    public string? Viewer { get; init; }
}

/// <summary>Managed webhook endpoints (schema <c>webhooks</c>).</summary>
public sealed record Webhooks
{
    /// <summary>Managed webhook endpoints, keyed by endpoint name.</summary>
    public IReadOnlyDictionary<string, WebhookEndpoint>? Endpoints { get; init; }
}

/// <summary>A managed webhook endpoint (Standard Webhooks: HMAC signing, retries, DLQ).</summary>
public sealed record WebhookEndpoint
{
    /// <summary>HTTPS target.</summary>
    public required string Url { get; init; }

    /// <summary>Secret name in the secret store (never the secret value itself).</summary>
    public required string SecretRef { get; init; }

    /// <summary>Human-readable description of the endpoint.</summary>
    public string? Description { get; init; }
}

/// <summary>A reusable message template referenced by email/notification actions (schema <c>$defs/template</c>).</summary>
public sealed record MessageTemplate
{
    /// <summary>Subject line; supports <c>{{...}}</c> interpolation.</summary>
    public string? Subject { get; init; }

    /// <summary>Inline body; supports <c>{{...}}</c> interpolation.</summary>
    public string? Body { get; init; }

    /// <summary>Relative path to a template file inside the descriptor bundle.</summary>
    public string? BodyFile { get; init; }
}

/// <summary>A reusable validation format defined by a regular expression (schema <c>$defs/namedFormat</c>).</summary>
public sealed record NamedFormat
{
    /// <summary>Regular expression the field value must match.</summary>
    public required string Pattern { get; init; }

    /// <summary>Human-readable description of the format.</summary>
    public string? Description { get; init; }
}
