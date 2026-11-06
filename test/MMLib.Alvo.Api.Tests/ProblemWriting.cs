using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>Writes a problem result the way the host would and reads back its status and document.</summary>
internal static class ProblemWriting
{
    /// <summary>The status code and the problem document <paramref name="result"/> writes.</summary>
    /// <param name="result">The result a refusal mapped to.</param>
    internal static Task<(int Status, JsonObject Document)> WrittenAsync(IResult result) =>
        ProblemDetailsTests.DocumentWrittenByAsync(result);
}
