using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// Reads a self-service request body under the Data API's own bounds (<see cref="BoundedJsonBody"/>: size, depth, member
/// count, duplicate names, a top-level object) and hands its members to a route's own judge.
/// </summary>
/// <remarks>
/// A refusal carries the Data API's codes and wording, so a client handles one vocabulary; a string holding half a
/// surrogate pair is judged by the route (<see cref="TextOf"/>), since no .NET string can hold it.
/// </remarks>
internal static class SelfServiceBody
{
    /// <summary>The body's top-level object, judged by <paramref name="judge"/>; or the bound it broke.</summary>
    /// <typeparam name="T">What the judge reads out of the body.</typeparam>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="judge">Reads the object into a value, or every reason it cannot.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static async Task<(T? Value, IReadOnlyList<AlvoViolation> Violations)> ReadAsync<T>(
        HttpRequest request,
        AlvoApiOptions options,
        Func<JsonElement, (T? Value, IReadOnlyList<AlvoViolation> Violations)> judge,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(judge);
        using var body = new MemoryStream();
        if (await BoundedJsonBody.ReadAsync(request, body, options, ct).ConfigureAwait(false) is { } refused)
        {
            return (null, [PayloadViolations.Body(refused, options)]);
        }

        using var document = JsonDocument.Parse(
            body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = options.MaxPayloadDepth });
        return judge(document.RootElement);
    }

    /// <summary>
    /// A JSON string's text, or <see langword="null"/> when it holds half a surrogate pair: legal JSON escape text no .NET
    /// string can hold, so reading it throws.
    /// </summary>
    /// <param name="value">A string element.</param>
    internal static string? TextOf(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The refusal for every top-level member of <paramref name="root"/> outside <paramref name="known"/>.</summary>
    /// <param name="root">The body's object.</param>
    /// <param name="known">The members the route reads.</param>
    internal static List<AlvoViolation> Unknown(JsonElement root, params string[] known) =>
        [.. root.EnumerateObject()
            .Where(member => !known.Contains(member.Name, StringComparer.Ordinal))
            .Select(member => PayloadViolations.UnknownField(member.Name))];
}
