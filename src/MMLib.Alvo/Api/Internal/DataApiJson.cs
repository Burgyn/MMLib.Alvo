using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The Data API's own <see cref="JsonSerializerOptions"/> — fixed by Alvo, never the host's.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row's keys are the descriptor's field names, and they are a contract, not presentation.</b> They
/// are the names every rule, every filter term, every scope and the OpenAPI schema use. Serializing a
/// row through the host's ambient options would put them under that host's
/// <see cref="JsonSerializerOptions.DictionaryKeyPolicy"/>: an embedded host that configures camelCase
/// for its own endpoints would silently rename <c>owner_id</c> to <c>ownerId</c> on the wire, while a
/// filter, a rule and the document all still say <c>owner_id</c>. The caller then cannot round-trip its
/// own response.
/// </para>
/// <para>
/// This is the same reasoning that gives the descriptor its own <c>AlvoDescriptorJsonContext</c>: a
/// format Alvo publishes is owned by Alvo. So both policies are explicitly <see langword="null"/>
/// (verbatim names, in both directions) and the envelope additionally pins its two members with
/// <see cref="JsonPropertyNameAttribute"/> — belt and braces, because the envelope's names are the one
/// part of the shape Alvo itself authored.
/// </para>
/// <para>
/// <see cref="JsonSerializerOptions.MaxDepth"/> is left at the reader's own configured depth rather than
/// set here: the request side's bound is <see cref="AlvoApiOptions.MaxPayloadDepth"/>, enforced while
/// parsing, and the response side serializes a flat dictionary Alvo built itself.
/// </para>
/// </remarks>
internal static class DataApiJson
{
    /// <summary>The options every Data API response is written with and every request body is bound through.</summary>
    internal static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        // Frozen at first use so nothing can mutate the contract at runtime; the resolver is populated
        // here rather than left unset, because a read-only instance without one cannot serialize anything.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
