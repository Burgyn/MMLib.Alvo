using Microsoft.OpenApi;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>The invitation bodies (S4 plan D9, D11): what an owner sends and reads back.</summary>
/// <remarks>
/// <b><c>code</c> is a member of exactly one answer</b>, <see cref="NewInvitationId"/> — the creation's 201; a listed
/// invitation (<see cref="InvitationId"/>) has no such member.
/// </remarks>
internal static partial class AccountsSchemas
{
    /// <summary>An owner's new invitation, as creating one sends it.</summary>
    internal const string InvitationRequestId = "authInvitationRequest";

    /// <summary>One invitation, as its tenant's owners list it — never its code.</summary>
    internal const string InvitationId = "authInvitation";

    /// <summary>A just-created invitation: <see cref="InvitationId"/>'s members and its code, shown this once.</summary>
    internal const string NewInvitationId = "authNewInvitation";

    private static void AddInvitationComponents(OpenApiDocument document)
    {
        document.AddComponent(InvitationRequestId, InvitationRequest());
        document.AddComponent(InvitationId, Invitation(withCode: false));
        document.AddComponent(NewInvitationId, Invitation(withCode: true));
    }

    private static OpenApiSchema InvitationRequest()
    {
        var schema = Object(
            "An owner's new invitation. Only `roles` is required; no other property is accepted.",
            ["roles"],
            ("roles", new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                UniqueItems = true,
                Description = "The tenant roles accepting it grants, each from `tenancy.selfService.assignableRoles`; `[]` grants none.",
                Items = new OpenApiSchema { Type = JsonSchemaType.String },
            }),
            ("isOwner", new OpenApiSchema
            {
                Type = JsonSchemaType.Boolean,
                Description = "Whether accepting it makes an owner; default false. Such an invitation admits one person and lives at most 7 days.",
            }),
            ("email", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "email",
                MaxLength = Tenants.SelfService.InvitationRequest.MaxEmailLength,
                Description = "Binds the invitation to the person whose provider vouches for this address (any case); default: anyone.",
            }),
            ("maxUses", new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                Minimum = "1",
                Maximum = "100",
                Description = "How many people may accept it; default 1.",
            }),
            ("expiresAt", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "date-time",
                Description = "When it stops admitting anyone: RFC 3339 with an offset, in the future, at most 30 days away; default 7 days from now.",
            }));
        schema.AdditionalPropertiesAllowed = false;
        return schema;
    }

    private static OpenApiSchema Invitation(bool withCode)
    {
        var properties = InvitationProperties();
        string[] required = ["id", "roles", "isOwner", "email", "maxUses", "uses", "expiresAt", "createdBy", "createdAt", "revokedAt", "status"];
        if (withCode)
        {
            properties.Insert(1, ("code", Text("The invitation's secret code (`alvo_inv_…`). Shown this once; hand it to the invitee.")));
            required = [.. required, "code"];
        }

        return Object(
            withCode ? "A just-created invitation, with its code." : "One invitation of a tenant, as its owners see it. Never its code.",
            required,
            [.. properties]);
    }

    private static List<(string Name, IOpenApiSchema Schema)> InvitationProperties() =>
    [
        ("id", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "The invitation's id; revoking it takes this." }),
        ("roles", Strings("The tenant roles accepting it grants under the applied descriptor.")),
        ("isOwner", new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Whether accepting it makes an owner." }),
        ("email", Nullable("The address it is bound to, lower-case, or null for anyone.")),
        ("maxUses", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Description = "How many people may accept it." }),
        ("uses", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Description = "How many have." }),
        ("expiresAt", Instant("When it stops admitting anyone.")),
        ("createdBy", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "The owner who created it." }),
        ("createdAt", Instant("When it was created.")),
        ("revokedAt", new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Format = "date-time",
            Description = "When it was first revoked, or null.",
        }),
        ("status", Enumerated(
            "`revoked`, else `usedUp`, else `expired`, else `active` — the first that holds, judged when the answer was made.",
            InvitationResponse.Active,
            InvitationResponse.Expired,
            InvitationResponse.UsedUp,
            InvitationResponse.Revoked)),
    ];

    private static OpenApiSchema Instant(string description)
        => new() { Type = JsonSchemaType.String, Format = "date-time", Description = description };
}
