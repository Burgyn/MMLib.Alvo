using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Which of a handler's bound arguments describe the request — route values, query, body and uploaded files, not services
/// or the request plumbing — so a key's fingerprint covers what was asked (Ruling 8): minimal-API filters run after binding,
/// so the raw body is already read. An uploaded file is described by its form name, file name, length and the SHA-256 of
/// its content (Ruling 52): two different uploads under one key are a conflict, never a replay.
/// </summary>
internal sealed class CommandInputs(IReadOnlyList<int> positions)
{
    private static readonly HashSet<Type> _supplied =
    [
        typeof(HttpContext), typeof(HttpRequest), typeof(HttpResponse), typeof(CancellationToken), typeof(ClaimsPrincipal),
        typeof(AlvoCommandContext), typeof(AlvoCaller), typeof(Stream), typeof(PipeReader),
    ];

    /// <summary>The fingerprinted parameters of <paramref name="method"/>.</summary>
    internal static CommandInputs Of(MethodInfo method, IServiceProviderIsService services) =>
        new([.. method.GetParameters().Where(parameter => Describes(parameter, services)).Select(parameter => parameter.Position)]);

    /// <summary>The request's fingerprint: the command, the route's <c>id</c>, and each input described, hashed.</summary>
    internal async Task<string> FingerprintAsync(
        CommandRegistration command, HttpContext http, IList<object?> arguments, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(command.Id).Append('\n').Append(http.Request.RouteValues["id"]);
        foreach (var position in positions)
        {
            text.Append('\n').Append(await DescribedAsync(arguments[position], json, cancellationToken).ConfigureAwait(false));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static async Task<string> DescribedAsync(object? value, JsonSerializerOptions json, CancellationToken cancellationToken) => value switch
    {
        null => "null",
        IFormFile file => await FileAsync(file, cancellationToken).ConfigureAwait(false),
        IFormFileCollection files => await FilesAsync(files, cancellationToken).ConfigureAwait(false),
        _ => JsonSerializer.Serialize(value, value.GetType(), json),
    };

    private static async Task<string> FilesAsync(IFormFileCollection files, CancellationToken cancellationToken)
    {
        var described = new List<string>(files.Count);
        foreach (var file in files)
        {
            described.Add(await FileAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return JsonSerializer.Serialize(described);
    }

    private static async Task<string> FileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var content = file.OpenReadStream();
        await using (content.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new[] { file.Name, file.FileName, file.Length.ToString(CultureInfo.InvariantCulture), Convert.ToHexStringLower(hash) });
        }
    }

    private static bool Describes(ParameterInfo parameter, IServiceProviderIsService services) =>
        !_supplied.Contains(parameter.ParameterType)
        && !parameter.GetCustomAttributes().Any(attribute => attribute is IFromServiceMetadata or FromKeyedServicesAttribute)
        && !services.IsService(parameter.ParameterType);
}
