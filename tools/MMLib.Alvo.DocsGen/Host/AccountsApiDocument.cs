using MMLib.Alvo.DocsGen.Markdown;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

/// <summary>The sign-in reference: the <c>/auth/v1</c> slice of the document a field-service host with accounts on generated.</summary>
internal static class AccountsApiDocument
{
    internal const string DocumentPath = "openapi/accounts-api.json";

    internal const string Prefix = "/auth/v1";

    internal const string Title = "Alvo sign-in, tenants and tokens — example (field-service)";

    internal const string Description =
        "The sign-in, self-service tenant and app-token routes Alvo generated for the field-service example descriptor "
        + "with Google sign-in configured. The routes exist only on a host that configures an identity provider. "
        + "The guides walk through them: [Sign-in and accounts](" + SiteLinks.BasePath + "/guides/sign-in/), "
        + "[Tenants, members and invitations](" + SiteLinks.BasePath + "/guides/tenants-and-invitations/) and "
        + "[Tokens for integrators](" + SiteLinks.BasePath + "/guides/tokens/).";

    /// <summary>Keeps the paths under <paramref name="prefix"/>, titles the document, and leaves its components as they are.</summary>
    internal static JsonNode Filter(JsonNode openApi, string prefix)
    {
        var document = openApi.DeepClone();
        var paths = document["paths"]!.AsObject();
        foreach (var key in paths.Select(path => path.Key).Where(key => !key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            paths.Remove(key);
        }

        var info = document["info"]!.AsObject();
        info["title"] = Title;
        info["description"] = Description;
        return document;
    }
}
