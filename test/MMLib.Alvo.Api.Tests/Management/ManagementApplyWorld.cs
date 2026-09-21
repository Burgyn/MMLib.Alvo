using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The request shapes every Management API write fact is built from — one apply, one export read, one body.
/// </summary>
/// <remarks>
/// <b>One definition, three suites.</b> <c>ManagementApplyTests</c>, <c>ManagementIdempotencyTests</c> and
/// <c>ManagementRollbackTests</c> all send the same two requests with different headers, and three private
/// copies of them would agree until the day one was edited. The parameters are the things a fact actually
/// varies: who is calling, what precondition they carry, and which of the two optional headers they send.
/// </remarks>
internal static class ManagementApplyWorld
{
    /// <summary>The project every write fact is measured over.</summary>
    internal const string DescriptorPath = ManagedFleet.Routes + "/descriptor";

    /// <summary>Sends one <c>PUT …/descriptor</c>.</summary>
    /// <param name="world">The running world.</param>
    /// <param name="key">The caller presenting the request.</param>
    /// <param name="descriptorJson">The descriptor to apply.</param>
    /// <param name="ifMatch">The <c>If-Match</c> tag to carry, or <see langword="null"/> to send none.</param>
    /// <param name="allowDestructive">Whether the body asks for a destructive plan.</param>
    /// <param name="query">The query string, including its leading <c>?</c>.</param>
    /// <param name="idempotencyKey">The <c>Idempotency-Key</c> to carry, or <see langword="null"/> for none.</param>
    /// <returns>The response, unread.</returns>
    internal static Task<HttpResponseMessage> ApplyAsync(
        AlvoApiWorld world,
        TestApiKey? key,
        string descriptorJson,
        string? ifMatch,
        bool allowDestructive = false,
        string query = "",
        string? idempotencyKey = null) =>
        world.SendAsync(
            HttpMethod.Put,
            DescriptorPath + query,
            key,
            body: Body(descriptorJson, allowDestructive),
            headers: Headers(ifMatch, idempotencyKey));

    /// <summary>The optional headers a write fact varies, as the sender takes them.</summary>
    /// <remarks>
    /// A sequence of pairs rather than a dictionary, so a fact can present one header <b>twice</b> — which is
    /// the ambiguity an <c>Idempotency-Key</c> route has to refuse, and which a dictionary cannot express.
    /// </remarks>
    /// <param name="ifMatch">The <c>If-Match</c> tag, or <see langword="null"/> to send none.</param>
    /// <param name="idempotencyKey">The <c>Idempotency-Key</c>, or <see langword="null"/> to send none.</param>
    /// <returns>The header lines to add.</returns>
    internal static IEnumerable<KeyValuePair<string, string>> Headers(string? ifMatch, string? idempotencyKey)
    {
        if (ifMatch is not null)
        {
            yield return new KeyValuePair<string, string>("If-Match", ifMatch);
        }

        if (idempotencyKey is not null)
        {
            yield return new KeyValuePair<string, string>("Idempotency-Key", idempotencyKey);
        }
    }

    /// <summary>One apply body, with the provenance every fact sends and nothing a fact has to read.</summary>
    /// <param name="descriptorJson">The descriptor to apply.</param>
    /// <param name="allowDestructive">Whether the body asks for a destructive plan.</param>
    /// <returns>The body.</returns>
    internal static JsonObject Body(string descriptorJson, bool allowDestructive = false) => new()
    {
        ["descriptorJson"] = descriptorJson,
        ["allowDestructive"] = allowDestructive,
        ["author"] = "the-suite",
        ["reason"] = "a fact",
    };

    /// <summary>The descriptor the project currently exports.</summary>
    /// <param name="world">The running world.</param>
    /// <param name="key">The caller reading it.</param>
    /// <returns>The stored descriptor text.</returns>
    internal static async Task<string> CurrentAsync(AlvoApiWorld world, TestApiKey? key) =>
        (await (await world.SendAsync(HttpMethod.Get, DescriptorPath, key)).ReadJsonObjectAsync())
            ["descriptorJson"]!.GetValue<string>();

    /// <summary>The project's current revision.</summary>
    /// <param name="world">The running world.</param>
    /// <param name="key">The caller reading it.</param>
    /// <returns>The revision the export reports.</returns>
    internal static async Task<int> RevisionAsync(AlvoApiWorld world, TestApiKey? key) =>
        (await (await world.SendAsync(HttpMethod.Get, DescriptorPath, key)).ReadJsonObjectAsync())
            ["revision"]!.GetValue<int>();
}
