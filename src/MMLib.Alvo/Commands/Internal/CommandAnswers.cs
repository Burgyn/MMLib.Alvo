using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MMLib.Alvo.Data;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// What a handler's result is as a replayable answer (Ruling 9): its status, and its value as JSON — a problem document
/// keeps its <c>application/problem+json</c>. A typed union (<c>Results&lt;…&gt;</c>) is read as the arm it answered. A
/// result that carries no status — a redirect, a file, a stream — a plain <see cref="string"/> (minimal APIs write it as
/// <c>text/plain</c>, a replay would answer JSON), a value that cannot be serialised, or a body past the cap is not
/// replayable. Headers — a <c>Created</c>'s <c>Location</c> among them — are not replayed (Ruling 49).
/// </summary>
internal static class CommandAnswers
{
    /// <summary>Gets the answer recorded for a handler that threw after its unit committed.</summary>
    internal static CommandAnswer Failed { get; } = new(StatusCodes.Status500InternalServerError, null);

    /// <summary>The media type a problem document is answered and replayed with.</summary>
    internal const string ProblemMediaType = "application/problem+json";

    /// <summary>The status a result answers with, or null when it does not say.</summary>
    internal static int? StatusOf(object? result) => Unwrapped(result) switch
    {
        null or EmptyHttpResult => StatusCodes.Status200OK,
        IStatusCodeHttpResult coded => coded.StatusCode ?? StatusCodes.Status200OK,
        IResult or string => null,
        _ => StatusCodes.Status200OK,
    };

    /// <summary>Gets a value indicating whether the result is a success: a 2xx, or a result whose status is not known.</summary>
    internal static bool IsSuccess(object? result) => StatusOf(result) is not { } status || status is >= 200 and < 300;

    /// <summary>The replayable answer of <paramref name="result"/>, or null when it cannot be replayed.</summary>
    internal static CommandAnswer? Of(object? result, JsonSerializerOptions json)
    {
        var answered = Unwrapped(result);
        if (StatusOf(answered) is not { } status)
        {
            return null;
        }

        var value = answered is IResult ? (answered as IValueHttpResult)?.Value : answered;
        if (value is null)
        {
            return new CommandAnswer(status, null);
        }

        return Serialized(value, json) is { } body && Encoding.UTF8.GetByteCount(body) <= CommandLedgerLimits.MaxAnswerBytes
            ? new CommandAnswer(status, body, value is ProblemDetails ? ProblemMediaType : null)
            : null;
    }

    private static object? Unwrapped(object? result)
    {
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        return result;
    }

    private static string? Serialized(object value, JsonSerializerOptions json)
    {
        try
        {
            return JsonSerializer.Serialize(value, value.GetType(), json);
        }
        catch (Exception refused) when (refused is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>A recorded answer, replayed: its status and its JSON body in its media type, nothing else.</summary>
internal sealed class ReplayedAnswer(CommandAnswer answer) : IResult, IStatusCodeHttpResult
{
    /// <inheritdoc/>
    public int? StatusCode => answer.Status;

    /// <inheritdoc/>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = answer.Status;
        if (answer.Body is { } body)
        {
            httpContext.Response.ContentType = (answer.MediaType ?? "application/json") + "; charset=utf-8";
            await httpContext.Response.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
