using Microsoft.OpenApi;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The two shapes a person is named in when a resource names its creator (#542 item 1): <see cref="PersonReferenceId"/>
/// to someone signed in, <see cref="PersonNameId"/> to the anonymous invitation preview — one key, <c>createdBy</c>, for both.
/// </summary>
/// <remarks>Two components rather than one with an optional <c>id</c>, so a generated client sees which answer carries an id.</remarks>
internal static partial class AccountsSchemas
{
    /// <summary>A person a resource names: the account id and the display name.</summary>
    internal const string PersonReferenceId = "authPersonReference";

    /// <summary>A person named to someone who has not signed in: the display name alone, never the id.</summary>
    internal const string PersonNameId = "authPersonName";

    private static void AddPersonComponents(OpenApiDocument document)
    {
        document.AddComponent(PersonReferenceId, PersonReference());
        document.AddComponent(PersonNameId, PersonName());
    }

    private static OpenApiSchema PersonReference() => Object(
        "A person: the account id and the display name `/auth/v1/user` answers, as it reads now. Never an email.",
        ["id", "name"],
        ("id", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "The account id." }),
        ("name", Nullable("The display name, or null when the person has none.")));

    private static OpenApiSchema PersonName() => Object(
        "A person named to someone who has not signed in: the display name alone, never the account id or an email.",
        ["name"],
        ("name", Nullable("The display name, or null when the person has none.")));

    private static OpenApiSchemaReference CreatedBy(OpenApiDocument document) => new(PersonReferenceId, document);
}
