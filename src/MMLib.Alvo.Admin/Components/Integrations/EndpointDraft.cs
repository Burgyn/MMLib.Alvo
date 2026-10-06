using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Secrets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>What the endpoint sheet holds, and what it refuses at a field before anything is written (spec §6.1).</summary>
/// <remarks>
/// <para>
/// <b>The URL rule is the apply's, by the same calls</b> — <c>Uri.TryCreate(…, Absolute)</c>, then <c>https</c>, or
/// <c>http</c> with <c>Uri.IsLoopback</c> (<c>AfterHookCompiler.ResolveTarget</c>/<c>IsDeliverable</c>); Host.Tests holds the
/// two to one answer. Stricter in time, stated in the hint: the apply checks an endpoint's URL only once a hook posts to it.
/// </para>
/// <para>
/// <b>The secret is a name, checked as one</b> (<see cref="SecretName"/>): the schema requires <c>secretRef</c> and this build
/// never reads it. A refusal never echoes what was pasted, which may have been the secret itself; nor does a URL refusal
/// echo the URL, which can be its own bearer token.
/// </para>
/// </remarks>
internal sealed partial class EndpointDraft
{
    /// <summary>The name input's id.</summary>
    public const string NameField = "endpoint-name";

    /// <summary>The URL input's id.</summary>
    public const string UrlField = "endpoint-url";

    /// <summary>The secret name input's id.</summary>
    public const string SecretField = "endpoint-secret";

    /// <summary>Gets or sets the endpoint's name — its key under <c>webhooks.endpoints</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL deliveries are posted to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the signing secret will be stored under.</summary>
    public string SecretRef { get; set; } = string.Empty;

    /// <summary>Gets or sets the description, which nothing in this build reads.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the secret name was typed, so the name no longer suggests it.</summary>
    public bool SecretTouched { get; set; }

    /// <summary>A draft holding a declared endpoint.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="declared">Its declaration.</param>
    /// <returns>The draft, its secret name counted as typed.</returns>
    public static EndpointDraft From(string name, JsonElement declared) => new()
    {
        Name = name,
        Url = DescriptorLens.TextOf(declared, "url") ?? string.Empty,
        SecretRef = DescriptorLens.TextOf(declared, "secretRef") ?? string.Empty,
        Description = DescriptorLens.TextOf(declared, "description") ?? string.Empty,
        SecretTouched = true,
    };

    /// <summary>The name, typed — and the suggested secret name with it, until that is typed itself.</summary>
    /// <param name="name">What was typed.</param>
    public void TypeName(string name)
    {
        Name = name;
        if (!SecretTouched)
        {
            SecretRef = name.Length > 0 ? $"{name}-signing-key" : string.Empty;
        }
    }

    /// <summary>The secret name, typed.</summary>
    /// <param name="secret">What was typed.</param>
    public void TypeSecret(string secret)
    {
        SecretRef = secret;
        SecretTouched = true;
    }

    /// <summary>Each field's refusal, in the order the sheet draws the fields.</summary>
    /// <param name="declared">The endpoint names the working copy declares.</param>
    /// <param name="editing">Whether an existing endpoint is edited, whose name is fixed.</param>
    /// <returns>Field id to refusal, one per refused field.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> Refusals(IReadOnlyCollection<string> declared, bool editing)
    {
        var refusals = new List<KeyValuePair<string, string>>();
        refusals.AddRefusal(NameField, editing ? null : NameRefusal(Name.Trim(), declared));
        refusals.AddRefusal(UrlField, UrlRefusal(Url.Trim()));
        refusals.AddRefusal(SecretField, SecretRefusal(SecretRef.Trim()));
        return refusals;
    }

    /// <summary>Why a name cannot be an endpoint's key, or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="declared">The names already declared.</param>
    /// <returns>The refusal, which never repeats a malformed name, or <see langword="null"/>.</returns>
    public static string? NameRefusal(string name, IReadOnlyCollection<string> declared)
    {
        if (!EndpointName().IsMatch(name))
        {
            /* Not repeated: a URL or a secret pasted into the wrong box would be shown back, as UrlRefusal and
               SecretRefusal never do. */
            return "That is not an endpoint name. A name is lower case, starts with a letter, and holds letters, digits "
                + "and dashes — up to 63 characters, such as 'billing-system'.";
        }

        return declared.Contains(name, StringComparer.Ordinal)
            ? $"An endpoint named '{name}' is already declared. Edit it, or choose another name."
            : null;
    }

    /// <summary>Why the apply would refuse this URL once a hook posts to it, or <see langword="null"/>.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The refusal, which never repeats the URL, or <see langword="null"/>.</returns>
    public static string? UrlRefusal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return "That is not an absolute URL, so no delivery could ever be attempted. Give one such as 'https://example.com/hooks/alvo'.";
        }

        return IsDeliverable(parsed)
            ? null
            : $"This URL uses '{parsed.Scheme}'. A delivery carries the record's complete image, unsigned, so it must be 'https' — "
                + "'http' is accepted only for a loopback host (localhost, 127.0.0.1, [::1]).";
    }

    /// <summary>The apply's own test: <c>https</c>, or <c>http</c> to a loopback host.</summary>
    /// <param name="url">An absolute URL.</param>
    /// <returns><see langword="true"/> when a delivery to it would be attempted.</returns>
    public static bool IsDeliverable(Uri url)
        => string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
           || (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && url.IsLoopback);

    /// <summary>Why this cannot be a secret's name, or <see langword="null"/> — never repeating what was typed.</summary>
    /// <param name="secret">The secret name.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? SecretRefusal(string secret)
        => SecretName.TryParse(secret, out _)
            ? null
            : "That is not a secret name. Write the name the signing secret will be stored under — lower case, starting with "
                + "a letter, such as 'billing-signing-key' — never the secret itself.";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointName();
}
