using Microsoft.OpenApi;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>What each self-service operation (spec §6.3, tag <c>tenants</c>) publishes.</summary>
/// <remarks>
/// <para>
/// <b>Every operation lists the 404 <c>feature-disabled</c></b> beside its other refusals: the routes are always mapped,
/// and an agent reading the document learns from it which descriptor key turns a route on.
/// </para>
/// <para>
/// <b>Every answer documents <c>Cache-Control: no-store</c> only</b> — the core's filter sets no <c>Pragma</c>, and these
/// answers carry no token, so RFC 6749 §5.1's pair does not apply.
/// </para>
/// </remarks>
internal static partial class AccountsDocumentation
{
    private const string TenantIdParameter = "tenantId";

    private static readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        [TenantIdParameter] = "The tenant's id, as `GET /tenants` lists it.",
        ["userId"] = "The member's account id, as the members list shows it; the person's own id to leave.",
        ["invitationId"] = "The invitation's id, as the invitations list shows it.",
    };

    /// <summary>Gives every documented path parameter its description.</summary>
    /// <param name="operation">The operation being described.</param>
    private static void DescribeParameters(OpenApiOperation operation)
    {
        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            if (parameter.Name is { } name && _parameters.TryGetValue(name, out var description))
            {
                parameter.Description = description;
            }
        }
    }

    private static Dictionary<string, Operation> WithTenantOperations(Dictionary<string, Operation> operations)
    {
        operations[SelfServiceEndpoints.ListOperationId] = new(
            "List my tenants",
            "The tenants the signed-in person belongs to, the personal one first, each with the tenant roles that count there "
            + "under the applied descriptor and whether the person owns it.",
            Bearer: true,
            _ => null,
            document => Answers(
                document,
                ("200", Response("The person's memberships.", PersonNoStore(), Media("application/json", Memberships(document)))),
                PersonUnauthenticated(document),
                NotAPerson(document),
                Disabled(document, "`feature-disabled`: the descriptor does not declare `tenancy.selfService`.")),
            SelfServiceEndpoints.Tag);
        operations[SelfServiceEndpoints.CreateOperationId] = new(
            "Create a shared tenant",
            "Creates a tenant others can join, with an id the server chooses. The person becomes its owner, holding "
            + "`createTenants.creatorRoles`. One account creates at most `createTenants.maxPerAccount` shared tenants, ever.",
            Bearer: true,
            document => Body("application/json", AccountsSchemas.TenantNameId, "The new tenant's name.", document),
            CreateResponses,
            SelfServiceEndpoints.Tag);
        operations[SelfServiceEndpoints.RenameOperationId] = new(
            "Rename a tenant",
            "Renames a tenant the person owns, a personal one included. Answers the person's membership in it.",
            Bearer: true,
            document => Body("application/json", AccountsSchemas.TenantNameId, "The tenant's new name.", document),
            RenameResponses,
            SelfServiceEndpoints.Tag);
        return WithMemberOperations(operations);
    }

    private static Dictionary<string, Operation> WithMemberOperations(Dictionary<string, Operation> operations)
    {
        operations[SelfServiceEndpoints.ListMembersOperationId] = new(
            "List a tenant's members",
            "The members of a tenant the person belongs to: display name, avatar, the tenant roles that count for them under "
            + "the applied descriptor, and whether they own it. Never an email.",
            Bearer: true,
            _ => null,
            ListMembersResponses,
            SelfServiceEndpoints.Tag);
        operations[SelfServiceEndpoints.UpdateMemberOperationId] = new(
            "Change a member's roles or ownership",
            "An owner changes another member's tenant roles (each from `tenancy.selfService.assignableRoles`) and/or "
            + "ownership; a part left out is kept. Nobody changes their own membership, and a tenant never loses its last owner.",
            Bearer: true,
            document => Body("application/json", AccountsSchemas.MemberUpdateId, "What changes.", document),
            UpdateMemberResponses,
            SelfServiceEndpoints.Tag);
        operations[SelfServiceEndpoints.RemoveMemberOperationId] = new(
            "Remove a member, or leave",
            "An owner removes another member; any member leaves by naming themself. The removed member's next request in "
            + "the tenant is refused. A personal tenant's owner never leaves it, and a tenant never loses its last owner.",
            Bearer: true,
            _ => null,
            RemoveMemberResponses,
            SelfServiceEndpoints.Tag);
        return WithInvitationOperations(operations);
    }

    private static OpenApiResponses ListMembersResponses(OpenApiDocument document) => Answers(
        document,
        ("200", Response("The tenant's members.", PersonNoStore(), Media("application/json", Members(document)))),
        PersonUnauthenticated(document),
        NotAPerson(document),
        NoSuchTenant(document));

    private static OpenApiResponses UpdateMemberResponses(OpenApiDocument document) => Answers(
        document,
        ("200", Response("The member, as changed.", PersonNoStore(), Media("application/json", AccountsSchemas.MemberId, document))),
        PersonUnauthenticated(document),
        OwnersOnly(document, "the person is no owner, or named themself — nobody changes their own membership"),
        NoSuchTenant(document, " or the member is no member of it"),
        MembershipConflict(document, lastOwner: null),
        Unsupported(document),
        TenantRefusal(
            "422",
            "`validation`: the body names neither `roles` nor `isOwner`, carries a property other than those two, or a role at `/roles/{i}` is "
            + "not in `tenancy.selfService.assignableRoles` (the detail lists them) or is named twice.",
            document));

    private static OpenApiResponses RemoveMemberResponses(OpenApiDocument document) => Answers(
        document,
        ("204", Response("The member was removed, or the person left.", PersonNoStore())),
        PersonUnauthenticated(document),
        OwnersOnly(document, "the person is no owner and named another member"),
        NoSuchTenant(document, " or the member is no member of it"),
        MembershipConflict(document, "would remove the tenant's last owner"));

    private static (string, OpenApiResponse) OwnersOnly(OpenApiDocument document, string why) => TenantRefusal(
        "403", $"`forbidden`: {why}; or the caller is not a signed-in person.", document);

    private static (string, OpenApiResponse) NoSuchTenant(OpenApiDocument document, string member = "") => Disabled(
        document,
        $"`not-found`: no such tenant, the person is no member of it{member} — one answer for all. `feature-disabled`: the "
        + "descriptor does not declare `tenancy.selfService`.");

    private static (string, OpenApiResponse) MembershipConflict(OpenApiDocument document, string? lastOwner) => TenantRefusal(
        "409",
        (lastOwner is null ? string.Empty : $"`last-owner`: the change {lastOwner}; make another member an owner first. ")
        + "`conflict`: the tenant is a personal one, whose one membership never changes.",
        document);

    private static OpenApiSchema Members(OpenApiDocument document) => new()
    {
        Type = JsonSchemaType.Array,
        Description = "The members, by joining.",
        Items = new OpenApiSchemaReference(AccountsSchemas.MemberId, document),
    };

    private static OpenApiResponses CreateResponses(OpenApiDocument document)
    {
        var (_, created) = MembershipAnswer(document, "201", "The tenant was created; the body is the person's owner membership.");
        created.Headers!["Location"] = Header("The new tenant's URL: `{prefix}/tenants/{tenantId}`.");
        return Answers(
            document,
            ("201", created),
            PersonUnauthenticated(document),
            NotAPerson(document),
            Disabled(
                document,
                "`feature-disabled`: the descriptor does not declare `tenancy.selfService` or `tenancy.selfService.createTenants`; "
                + "`descriptorKey` names which."),
            TenantRefusal(
                "409",
                "`tenant-limit`: the person has already created `maxPerAccount` shared tenants (the member carries the cap).",
                document),
            Unsupported(document),
            Invalid(document),
            CreateRateLimited(document));
    }

    private static OpenApiResponses RenameResponses(OpenApiDocument document) => Answers(
        document,
        MembershipAnswer(document, "200", "The person's membership in the renamed tenant."),
        PersonUnauthenticated(document),
        TenantRefusal(
            "403",
            "`forbidden`: the person is a member but not an owner, or the caller is not a signed-in person.",
            document),
        Disabled(
            document,
            "`not-found`: no such tenant, or the person is no member of it — the two are one answer. `feature-disabled`: the "
            + "descriptor does not declare `tenancy.selfService`."),
        Unsupported(document),
        Invalid(document));

    private static (string, OpenApiResponse) MembershipAnswer(OpenApiDocument document, string status, string description)
        => (status, Response(description, PersonNoStore(), Media("application/json", AccountsSchemas.MembershipId, document)));

    private static (string, OpenApiResponse) PersonUnauthenticated(OpenApiDocument document)
    {
        var (status, response) = TenantRefusal(
            "401",
            "`unauthenticated`: no access token was presented, or it cannot be used. `WWW-Authenticate` is the RFC 6750 "
            + "challenge: bare `Bearer` when none was presented, otherwise with `error=\"invalid_token\"` or "
            + "`error=\"invalid_request\"`.",
            document);
        response.Headers!["WWW-Authenticate"] = Header("The RFC 6750 Bearer challenge.");
        return (status, response);
    }

    private static (string, OpenApiResponse) NotAPerson(OpenApiDocument document) => TenantRefusal(
        "403", "`forbidden`: an API key or an integration's token was presented; only a signed-in person manages tenants.", document);

    private static (string, OpenApiResponse) Disabled(OpenApiDocument document, string description)
        => TenantRefusal("404", description, document);

    private static (string, OpenApiResponse) Unsupported(OpenApiDocument document)
        => TenantRefusal("415", "`unsupported-media-type`: the body is not `application/json`.", document);

    private static (string, OpenApiResponse) Invalid(OpenApiDocument document) => TenantRefusal(
        "422", "`validation`: the name is missing, longer than 100 characters or not text, or the body carries a property other than `name`.", document);

    private static (string, OpenApiResponse) CreateRateLimited(OpenApiDocument document)
    {
        var (status, response) = TenantRefusal(
            "429",
            "`rate-limited`: this client created more tenants this minute than `Alvo:Api:RateLimits:Create` allows. Wait for "
            + "`Retry-After`, then send the request again.",
            document);
        response.Headers!["Retry-After"] = Header("Whole seconds until the window admits this client again.", "60");
        return (status, response);
    }

    private static (string, OpenApiResponse) TenantRefusal(string status, string description, OpenApiDocument document)
        => (status, Response(description, PersonNoStore(), Media(AlvoDocumentTransformer.ProblemMediaType, ProblemComponents.DocumentId, document)));

    private static Dictionary<string, OpenApiMediaType> Media(string mediaType, IOpenApiSchema schema)
        => new(StringComparer.Ordinal) { [mediaType] = new OpenApiMediaType { Schema = schema } };

    private static OpenApiSchema Memberships(OpenApiDocument document) => new()
    {
        Type = JsonSchemaType.Array,
        Description = "The memberships, the personal one first, then by joining.",
        Items = new OpenApiSchemaReference(AccountsSchemas.MembershipId, document),
    };

    private static Dictionary<string, IOpenApiHeader> PersonNoStore() => new(StringComparer.Ordinal)
    {
        ["Cache-Control"] = Header("Always `no-store`: every answer here is about one person.", NoStoreValue),
    };
}
