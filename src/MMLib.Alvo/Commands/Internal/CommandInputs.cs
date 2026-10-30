using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Which of a handler's bound arguments describe the request — route values, query and body, not services or the request
/// plumbing — so a key's fingerprint covers what was asked (Ruling 8): minimal-API filters run after binding, so the raw
/// body is already read.
/// </summary>
internal sealed class CommandInputs(IReadOnlyList<int> positions)
{
    private static readonly HashSet<Type> _supplied =
    [
        typeof(HttpContext), typeof(HttpRequest), typeof(HttpResponse), typeof(CancellationToken), typeof(ClaimsPrincipal),
        typeof(AlvoCommandContext), typeof(AlvoCaller), typeof(Stream), typeof(PipeReader), typeof(IFormFileCollection),
    ];

    /// <summary>The fingerprinted parameters of <paramref name="method"/>.</summary>
    internal static CommandInputs Of(MethodInfo method, IServiceProviderIsService services) =>
        new([.. method.GetParameters().Where(parameter => Describes(parameter, services)).Select(parameter => parameter.Position)]);

    /// <summary>The request's fingerprint: the command, the route's <c>id</c>, and each input as JSON, hashed.</summary>
    internal string Fingerprint(CommandRegistration command, HttpContext http, IList<object?> arguments, JsonSerializerOptions json)
    {
        var text = new StringBuilder(command.Id).Append('\n').Append(http.Request.RouteValues["id"]);
        foreach (var position in positions)
        {
            text.Append('\n').Append(arguments[position] is { } value ? JsonSerializer.Serialize(value, value.GetType(), json) : "null");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static bool Describes(ParameterInfo parameter, IServiceProviderIsService services) =>
        !_supplied.Contains(parameter.ParameterType)
        && !parameter.GetCustomAttributes().Any(attribute => attribute is IFromServiceMetadata or FromKeyedServicesAttribute)
        && !services.IsService(parameter.ParameterType);
}
